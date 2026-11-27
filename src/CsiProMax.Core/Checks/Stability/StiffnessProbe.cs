namespace CsiProMax.Checks.Stability;

/// <summary>
/// A light linear stiffness check, the way the CSI programs find an unstable model when they start an analysis: the
/// stiffness of every member is put together and factored, and an equation whose pivot comes out (next to) zero is a
/// direction a joint or a floor can move in with nothing resisting it. No load is applied and nothing is solved for;
/// only the stiffness is looked at, so it takes a second where a run takes minutes.
///
/// It is an approximation made for finding mechanisms, not for results: frames are 3D beams with their releases,
/// shells are tied to their corners by members of the shell's own thickness (membranes in-plane only, plates bending
/// only), rigid diaphragms tie their joints in plan exactly. As in the CSI programs, a direction that no member gives
/// any stiffness to at all (the rotation of a joint where every member is pinned) is held by the program and is not
/// an instability.
/// </summary>
public static class StiffnessProbe
{
    /// <summary>A pivot that lost more digits than this (as a power of ten) is taken as zero: the CSI programs warn from 6 and stop near 11.</summary>
    public const double Lost = 1e-10;

    /// <summary>Elements the probe works with: a straight member between two joints, with its stiffness numbers.</summary>
    public sealed record Bar(int I, int J, double EA, double EI2, double EI3, double GJ, bool[] Releases, string Owner);

    /// <summary>A direction that was found free: a joint's (Joint ≥ 0, Dof 0..5) or a rigid floor's (Floor ≥ 0, Dof 0 UX, 1 UY, 5 RZ).</summary>
    public sealed record Free(int Joint, int Floor, int Dof, double Pivot);

    /// <param name="Held">Joint translations no member gives any stiffness to, held as the program holds them (holdUnstiffened).</param>
    public sealed record Result(bool Ran, string Why, int Equations, long Profile, IReadOnlyList<Free> Free, bool IllConditioned, int Held = 0);

    /// <summary>A rigid floor: its joints move together in plan (UX, UY, RZ) about its centre.</summary>
    public sealed record Floor(IReadOnlyList<int> Joints, double X, double Y);

    /// <param name="xyz">Each joint's position.</param>
    /// <param name="fixedDof">Each joint's 6 restrained directions (a restrained direction has no equation).</param>
    /// <param name="springDof">A support spring in that direction (adds stiffness of the order of the joint's own).</param>
    /// <param name="active">The directions the program solves for (all six; SAFE: UZ, RX, RY).</param>
    /// <param name="holdUnstiffened">A joint translation no member gives any stiffness to (the corner of a membrane
    /// floor up and down, a joint only a pinned truss reaches across it) is held, not reported: ETABS and SAFE carry a
    /// membrane floor's load to its beams and leave such directions out of the analysis, as every CSI program does with
    /// the rotations of a pinned joint. SAP2000 analyses every joint it is given, so there it is reported.</param>
    public static Result Run(IReadOnlyList<(double X, double Y, double Z)> xyz, bool[][] fixedDof, bool[][] springDof, IReadOnlyList<Bar> bars,
        IReadOnlyList<Floor> floors, bool[] active, int mostEquations = 60000, long mostProfile = 25_000_000, bool holdUnstiffened = false)
    {
        var n = xyz.Count;
        // Equations: a joint's free directions, except those a rigid floor carries, then each floor's UX, UY, RZ.
        var floorOf = new int[n];
        Array.Fill(floorOf, -1);
        for (var f = 0; f < floors.Count; f++) foreach (var j in floors[f].Joints) floorOf[j] = f;
        static bool InPlan(int d) => d is 0 or 1 or 5;
        var eq = new int[n * 6];
        Array.Fill(eq, -1);
        var owners = new List<(int Joint, int Floor, int Dof)>();
        for (var j = 0; j < n; j++)
            for (var d = 0; d < 6; d++)
            {
                if (!active[d] || fixedDof[j][d] && !(floorOf[j] >= 0 && InPlan(d))) continue;
                if (floorOf[j] >= 0 && InPlan(d)) continue;
                eq[j * 6 + d] = owners.Count;
                owners.Add((j, -1, d));
            }
        var floorEq = new int[floors.Count * 3];
        for (var f = 0; f < floors.Count; f++)
            for (var k = 0; k < 3; k++)
            {
                var d = k == 2 ? 5 : k;
                if (!active[d]) { floorEq[f * 3 + k] = -1; continue; }
                floorEq[f * 3 + k] = owners.Count;
                owners.Add((-1, f, d));
            }
        var neq = owners.Count;
        if (neq == 0) return new Result(true, "", 0, 0, [], false);
        if (neq > mostEquations) return new Result(false, $"the model has {neq} equations, more than the {mostEquations} this check takes", neq, 0, [], false);

        // How each joint direction is made of equations: itself, or its floor's three (u = u0 - dy*rz, v = v0 + dx*rz, rz).
        (int Eq, double C)[] Map(int j, int d)
        {
            if (floorOf[j] >= 0 && InPlan(d))
            {
                var f = floors[floorOf[j]];
                var ux = floorEq[floorOf[j] * 3]; var uy = floorEq[floorOf[j] * 3 + 1]; var rz = floorEq[floorOf[j] * 3 + 2];
                var list = new List<(int, double)>();
                switch (d)
                {
                    case 0: if (ux >= 0) list.Add((ux, 1)); if (rz >= 0) list.Add((rz, -(xyz[j].Y - f.Y))); break;
                    case 1: if (uy >= 0) list.Add((uy, 1)); if (rz >= 0) list.Add((rz, xyz[j].X - f.X)); break;
                    default: if (rz >= 0) list.Add((rz, 1)); break;
                }
                return [.. list];
            }
            var e = eq[j * 6 + d];
            return e >= 0 ? [(e, 1)] : [];
        }

        // Element matrices first (global, condensed for releases), so the ordering and the profile can be made.
        var elements = new List<(int[] Joints, double[,] K)>(bars.Count);
        foreach (var b in bars)
        {
            var k = BarStiffness(xyz[b.I], xyz[b.J], b);
            if (k is not null) elements.Add(([b.I, b.J], k));
        }
        var maps = new (int Eq, double C)[n * 6][];
        for (var j = 0; j < n; j++) for (var d = 0; d < 6; d++) maps[j * 6 + d] = Map(j, d);

        // Which equations meet (for the ordering).
        var adjacency = new HashSet<int>[neq];
        for (var i = 0; i < neq; i++) adjacency[i] = [];
        foreach (var (joints, _) in elements)
        {
            var eqs = joints.SelectMany(j => Enumerable.Range(0, 6).SelectMany(d => maps[j * 6 + d].Select(m => m.Eq))).Distinct().ToArray();
            foreach (var a in eqs) foreach (var c in eqs) if (a != c) adjacency[a].Add(c);
        }
        var order = ReverseCuthillMcKee(adjacency);
        var position = new int[neq];
        for (var i = 0; i < neq; i++) position[order[i]] = i;

        // Profile: the first row of each column.
        var first = new int[neq];
        for (var i = 0; i < neq; i++) first[i] = i;
        for (var a = 0; a < neq; a++)
            foreach (var c in adjacency[a])
            {
                int pa = position[a], pc = position[c];
                if (pa < pc && pa < first[pc]) first[pc] = pa;
            }
        var start = new long[neq + 1];
        for (var i = 0; i < neq; i++) start[i + 1] = start[i] + (i - first[i] + 1);
        var profile = start[neq];
        if (profile > mostProfile) return new Result(false, $"the stiffness would take {profile:N0} numbers, more than this check holds", neq, profile, [], false);

        var k2 = new double[profile];
        var elementDiagonal = new double[neq];
        void Add(int a, int c, double v)
        {
            int pa = position[a], pc = position[c];
            if (pa > pc) (pa, pc) = (pc, pa);
            k2[start[pc] + (pa - first[pc])] += v;
        }
        foreach (var (joints, k) in elements)
        {
            for (var r = 0; r < 12; r++)
            {
                var mr = maps[joints[r / 6] * 6 + r % 6];
                if (mr.Length == 0) continue;
                for (var s = 0; s < 12; s++)
                {
                    var v = k[r, s];
                    if (v == 0) continue;
                    var ms = maps[joints[s / 6] * 6 + s % 6];
                    foreach (var (ea, ca) in mr)
                        foreach (var (eb, cb) in ms)
                        {
                            if (position[ea] > position[eb]) continue;
                            Add(ea, eb, ca * cb * v);
                            if (ea == eb) elementDiagonal[ea] += ca * cb * v;
                        }
                }
            }
        }
        // Supports with springs, and joints held in a direction a floor carries: stiff springs of the joint's own order.
        var typical = elementDiagonal.Where(v => v > 0).DefaultIfEmpty(1).Average();
        for (var j = 0; j < n; j++)
            for (var d = 0; d < 6; d++)
            {
                var held = fixedDof[j][d] && floorOf[j] >= 0 && InPlan(d);
                if (!held && !springDof[j][d]) continue;
                var m = maps[j * 6 + d];
                var stiffness = held ? 1e3 * typical : Math.Max(typical * 1e-2, 1e-12);
                foreach (var (ea, ca) in m)
                    foreach (var (eb, cb) in m)
                    {
                        if (position[ea] > position[eb]) continue;
                        Add(ea, eb, ca * cb * stiffness);
                        // A support spring is real stiffness; the tie that holds a joint inside a floor is not counted.
                        if (ea == eb && !held) elementDiagonal[ea] += ca * cb * stiffness;
                    }
            }

        // LDLt by columns (skyline, Bathe's active column method). A zero pivot is noted and replaced by the equation's
        // own stiffness, as if it were held there, so the rest still factors and every free direction is found.
        var free = new List<Free>();
        var diag = new double[neq];
        var ill = false;
        var heldCount = 0;
        for (var pj = 0; pj < neq; pj++)
        {
            var fj = first[pj];
            var col = start[pj];
            for (var pi = fj + 1; pi < pj; pi++)
            {
                var fi = first[pi];
                var from = Math.Max(fi, fj);
                double sum = 0;
                var ci = start[pi];
                for (var r = from; r < pi; r++) sum += k2[ci + (r - fi)] * k2[col + (r - fj)];
                k2[col + (pi - fj)] -= sum;
            }
            double dj = k2[col + (pj - fj)];
            for (var r = fj; r < pj; r++)
            {
                var g = k2[col + (r - fj)];
                var l = g / diag[r];
                dj -= l * g;
                k2[col + (r - fj)] = l;
            }
            var own = elementDiagonal[order[pj]];
            var (joint, floor, dof) = owners[order[pj]];
            if (own <= 0 && floor < 0 && dof >= 3)
            {
                // No member gives this rotation any stiffness (every member is pinned there): the program holds it.
                diag[pj] = 1;
            }
            else if (own <= 0 && floor < 0 && holdUnstiffened)
            {
                heldCount++;
                diag[pj] = 1;
            }
            else if (own <= 0)
            {
                // A translation, or a floor, with no stiffness at all: it has mass and load, and nothing holds it.
                free.Add(new Free(joint, floor, dof, 0));
                diag[pj] = typical;
            }
            else if (dj <= own * Lost)
            {
                free.Add(new Free(joint, floor, dof, own == 0 ? 0 : dj / own));
                diag[pj] = own;
            }
            else
            {
                if (dj < own * 1e-7) ill = true;
                diag[pj] = dj;
            }
            k2[col + (pj - fj)] = diag[pj];
        }
        return new Result(true, "", neq, profile, free, ill, heldCount);
    }

    /// <summary>Reverse Cuthill-McKee: an order of the equations that keeps the matrix narrow, part by part.</summary>
    public static int[] ReverseCuthillMcKee(HashSet<int>[] adjacency)
    {
        var n = adjacency.Length;
        var seen = new bool[n];
        var order = new List<int>(n);
        var degree = adjacency.Select(a => a.Count).ToArray();
        foreach (var seed in Enumerable.Range(0, n).OrderBy(i => degree[i]))
        {
            if (seen[seed]) continue;
            var queue = new Queue<int>();
            queue.Enqueue(seed); seen[seed] = true;
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                order.Add(v);
                foreach (var w in adjacency[v].Where(w => !seen[w]).OrderBy(w => degree[w]))
                {
                    seen[w] = true;
                    queue.Enqueue(w);
                }
            }
        }
        order.Reverse();
        return [.. order];
    }

    /// <summary>The 12x12 stiffness of a 3D member in global directions, with its end releases condensed out. Null when it has no length.</summary>
    public static double[,]? BarStiffness((double X, double Y, double Z) a, (double X, double Y, double Z) b, Bar bar)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        var L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (L <= 0) return null;
        double[] v1 = [dx / L, dy / L, dz / L];
        double[] v2, v3;
        if (Math.Sqrt(v1[0] * v1[0] + v1[1] * v1[1]) < 1e-3)
        {
            // Vertical: local 2 along global X, as the CSI programs do.
            v2 = [1, 0, 0];
            v3 = Cross(v1, v2);
        }
        else
        {
            v3 = Normalize(Cross(v1, [0, 0, 1]));
            v2 = Cross(v3, v1);
        }
        var k = new double[12, 12];
        double ea = bar.EA / L, gj = bar.GJ / L;
        double a3 = 12 * bar.EI3 / (L * L * L), b3 = 6 * bar.EI3 / (L * L), c3 = 4 * bar.EI3 / L, d3 = 2 * bar.EI3 / L;
        double a2 = 12 * bar.EI2 / (L * L * L), b2 = 6 * bar.EI2 / (L * L), c2 = 4 * bar.EI2 / L, d2 = 2 * bar.EI2 / L;
        void S(int r, int c, double v) { k[r, c] += v; if (r != c) k[c, r] += v; }
        // Local order per end: u1 u2 u3 r1 r2 r3.
        S(0, 0, ea); S(6, 6, ea); S(0, 6, -ea);
        S(3, 3, gj); S(9, 9, gj); S(3, 9, -gj);
        // Bending in the 1-2 plane (about 3): u2, r3.
        S(1, 1, a3); S(7, 7, a3); S(1, 7, -a3); S(1, 5, b3); S(1, 11, b3); S(7, 5, -b3); S(7, 11, -b3); S(5, 5, c3); S(11, 11, c3); S(5, 11, d3);
        // Bending in the 1-3 plane (about 2): u3, r2.
        S(2, 2, a2); S(8, 8, a2); S(2, 8, -a2); S(2, 4, -b2); S(2, 10, -b2); S(8, 4, b2); S(8, 10, b2); S(4, 4, c2); S(10, 10, c2); S(4, 10, d2);

        // Releases, in the CSI programs' order (P V2 V3 T M2 M3) mapped to the local order (u1 u2 u3 r1 r2 r3): the same.
        for (var r = 0; r < 12; r++)
        {
            if (r >= bar.Releases.Length || !bar.Releases[r]) continue;
            var p = k[r, r];
            if (p > 1e-14 * (Math.Abs(ea) + Math.Abs(c3) + Math.Abs(c2) + 1e-300))
                for (var i = 0; i < 12; i++)
                {
                    if (i == r) continue;
                    var kir = k[i, r];
                    if (kir == 0) continue;
                    for (var j = 0; j < 12; j++) if (j != r) k[i, j] -= kir * k[r, j] / p;
                }
            for (var i = 0; i < 12; i++) { k[i, r] = 0; k[r, i] = 0; }
        }

        // What the condensing left as round-off (a pinned-pinned member's sideways stiffness) is zero.
        var largest = 0.0;
        foreach (var v in k) largest = Math.Max(largest, Math.Abs(v));
        for (var i = 0; i < 12; i++) for (var j = 0; j < 12; j++) if (Math.Abs(k[i, j]) <= largest * 1e-11) k[i, j] = 0;

        // To global: T = blockdiag(R, R, R, R), R rows are v1, v2, v3. K = Tt k T.
        double[,] R = { { v1[0], v1[1], v1[2] }, { v2[0], v2[1], v2[2] }, { v3[0], v3[1], v3[2] } };
        var t = new double[12, 12];
        for (var blk = 0; blk < 4; blk++)
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) t[blk * 3 + i, blk * 3 + j] = R[i, j];
        var kt = new double[12, 12];
        for (var i = 0; i < 12; i++)
            for (var j = 0; j < 12; j++)
            {
                double s = 0;
                for (var m = 0; m < 12; m++) if (k[i, m] != 0) s += k[i, m] * t[m, j];
                kt[i, j] = s;
            }
        var g = new double[12, 12];
        for (var i = 0; i < 12; i++)
            for (var j = 0; j < 12; j++)
            {
                double s = 0;
                for (var m = 0; m < 12; m++) if (t[m, i] != 0) s += t[m, i] * kt[m, j];
                g[i, j] = s;
            }
        return g;
    }

    private static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static double[] Normalize(double[] v)
    {
        var l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return l > 0 ? [v[0] / l, v[1] / l, v[2] / l] : v;
    }
}
