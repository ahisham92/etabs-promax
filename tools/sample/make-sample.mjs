// Makes the Viewer's sample: a 15-story steel office tower with a concrete core, and result and design tables shaped
// the way ETABS writes them. The numbers are made up to look like a real analysis; nothing here is analysed.
//
//   node tools/sample/make-sample.mjs src/CsiProMax.Viewer/viewer.html out/viewer-sample.html [out/sample-pack.json]
import fs from "node:fs";

const [, , viewerPath, outHtml, outJson] = process.argv;
if (!viewerPath || !outHtml) { console.error("usage: make-sample.mjs viewer.html out.html [pack.json]"); process.exit(2); }

const r3 = v => Math.round(v * 1000) / 1000, r5 = v => Math.round(v * 1e5) / 1e5, s = v => String(Math.round(v * 1e4) / 1e4);
const X = [0, 8, 16, 24, 32, 40, 48], Y = [0, 7.5, 15, 22.5, 30], XL = "ABCDEFG", YL = "12345";
const N = 15, heights = [4.5, ...Array(N - 1).fill(3.6)];
const stories = [{name: "Base", elevation: 0, height: 0}];
heights.forEach((h, i) => stories.push({name: "Story" + (i + 1), elevation: r3(stories[i].elevation + h), height: h}));
const H = stories[N].elevation, core = {x0: 20, x1: 28, y0: 11, y1: 19}, xc = 24, yc = 15;
const inCore = (x, y) => x > core.x0 - 1e-6 && x < core.x1 + 1e-6 && y > core.y0 - 1e-6 && y < core.y1 + 1e-6;

// points
const pts = new Map(), points = [];
const pt = (x, y, z) => { const k = `${r3(x)},${r3(y)},${r3(z)}`; if (!pts.has(k)) { const n = String(pts.size + 1); pts.set(k, n); points.push([n, r3(x), r3(y), r3(z)]); } return pts.get(k); };
const MX = []; for (let i = 0; i + 1 < X.length; i++) MX.push(X[i], (X[i] + X[i + 1]) / 2); MX.push(X[X.length - 1]);
const MY = []; for (let i = 0; i + 1 < Y.length; i++) MY.push(Y[i], (Y[i] + Y[i + 1]) / 2); MY.push(Y[Y.length - 1]);

const colSec = i => i <= 4 ? "W14X257" : i <= 8 ? "W14X176" : i <= 12 ? "W14X109" : "W14X74";
const frames = [], areas = [];
let nc = 0, nb = 0, nd = 0, nf = 0, nw = 0;
for (let i = 1; i <= N; i++) {
  const z0 = stories[i - 1].elevation, z = stories[i].elevation, st = stories[i].name;
  for (const x of X) for (const y of Y) { if (inCore(x, y)) continue; pt(x, y, z0); pt(x, y, z); frames.push([`C${++nc}`, "column", colSec(i), st, x, y, z0, x, y, z]); }
  // girders along X, beams along Y on the grid, fillers along Y at mid-bay
  for (const y of Y) for (let k = 0; k + 1 < X.length; k++) { const a = X[k], b = X[k + 1]; if (inCore((a + b) / 2, y)) continue; frames.push([`B${++nb}`, "beam", "W24X76", st, a, y, z, b, y, z]); }
  for (const x of X) for (let k = 0; k + 1 < Y.length; k++) { const a = Y[k], b = Y[k + 1]; if (inCore(x, (a + b) / 2)) continue; frames.push([`B${++nb}`, "beam", "W21X50", st, x, a, z, x, b, z]); }
  for (let k = 0; k + 1 < X.length; k++) { const x = (X[k] + X[k + 1]) / 2; for (let j = 0; j + 1 < Y.length; j++) { const a = Y[j], b = Y[j + 1]; if (inCore(x, (a + b) / 2)) continue; frames.push([`B${++nb}`, "beam", "W16X31", st, x, a, z, x, b, z]); } }
  // podium chevron braces on grids 1 and 5
  if (i <= 3) for (const [y, a, b] of [[0, 16, 24], [30, 24, 32]]) { const m = (a + b) / 2; frames.push([`D${++nd}`, "brace", "HSS8X8X1/2", st, a, y, z0, m, y, z], [`D${++nd}`, "brace", "HSS8X8X1/2", st, b, y, z0, m, y, z]); }
  // the floor, two by two panels in every bay
  for (let k = 0; k + 1 < MX.length; k++) for (let j = 0; j + 1 < MY.length; j++) {
    const a = MX[k], b = MX[k + 1], c = MY[j], d = MY[j + 1];
    if (inCore((a + b) / 2, (c + d) / 2) && inCore(a, c) && inCore(b, d)) continue;
    [[a, c], [b, c], [b, d], [a, d]].forEach(q => pt(q[0], q[1], z));
    areas.push({n: `F${++nf}`, k: "floor", s: "Deck150", c: [a, c, z, b, c, z, b, d, z, a, d, z]});
  }
  // the core: four walls
  const C = [[core.x0, core.y0], [core.x1, core.y0], [core.x1, core.y1], [core.x0, core.y1]];
  for (let k = 0; k < 4; k++) { const p = C[k], q = C[(k + 1) % 4]; [pt(p[0], p[1], z0), pt(q[0], q[1], z0), pt(q[0], q[1], z), pt(p[0], p[1], z)]; areas.push({n: `W${++nw}`, k: "wall", s: k % 2 ? "Wall400" : "Wall350", c: [p[0], p[1], z0, q[0], q[1], z0, q[0], q[1], z, p[0], p[1], z]}); }
}
for (const f of frames) { pt(f[4], f[5], f[6]); pt(f[7], f[8], f[9]); }
const at = (x, y, z) => pts.get(`${r3(x)},${r3(y)},${r3(z)}`);
const base = points.filter(p => p[3] === 0);
const supports = base.map(p => [p[1], p[2], 0, "fixed"]);

// displacements
const frac = (v, L) => { let i = 0; while (i + 1 < L.length && v > L[i + 1] + 1e-9) i++; const a = L[i], b = L[Math.min(i + 1, L.length - 1)]; return b > a ? (v - a) / (b - a) : 0; };
const sag = (x, y) => { const sx = Math.sin(Math.PI * frac(x, X)), sy = Math.sin(Math.PI * frac(y, Y)); return (0.0055 * (sx + sy) + 0.013 * sx * sy) * (inCore(x, y) ? 0.25 : 1); };
const lat = r => 0.55 * r + 0.45 * r * r * (3 - r) / 2;
const shapes = {
  DEAD: (x, y, z) => { const r = z / H, onCol = X.includes(x) && Y.includes(y); return [0.0004 * r * (x - xc) / 24, 0.0003 * r * (y - yc) / 15, -(0.006 * r * (inCore(x, y) ? 0.6 : 1)) - (onCol ? 0 : sag(x, y)) * (z > 0 ? 1 : 0)]; },
  LIVE: (x, y, z) => { const d = shapes.DEAD(x, y, z); return [d[0] * 0.35, d[1] * 0.35, d[2] * 0.38]; },
  EQX: (x, y, z) => { const r = z / H, t = 0.0009 * lat(r); return [0.088 * lat(r) - t * (y - yc), t * (x - xc) + 0.004 * lat(r), 0.0016 * r * (x - xc) / 24]; },
  EQY: (x, y, z) => { const r = z / H, t = 0.0011 * lat(r); return [-t * (y - yc) + 0.003 * lat(r), 0.104 * lat(r) + t * (x - xc), 0.0014 * r * (y - yc) / 15]; },
};
const modes = [[2.84, (x, y, z) => [0.0006 * lat(z / H), 0.0108 * lat(z / H), 0]], [2.41, (x, y, z) => [0.0109 * lat(z / H), -0.0005 * lat(z / H), 0]], [1.62, (x, y, z) => { const t = 0.0006 * lat(z / H); return [-t * (y - yc), t * (x - xc), 0]; }]];
const combos = {ULS1: [["DEAD", 1.2], ["LIVE", 1.6]], ULS2: [["DEAD", 1.2], ["LIVE", 1.0], ["EQX", 1.0]], ULS3: [["DEAD", 1.2], ["LIVE", 1.0], ["EQY", 1.0]]};
const jd = {name: "Joint Displacements", fields: ["Story", "Label", "UniqueName", "OutputCase", "CaseType", "StepType", "StepNum", "Ux", "Uy", "Uz"], rows: []};
const storyOf = z => (stories.find(st => Math.abs(st.elevation - z) < 1e-6) || {name: ""}).name;
for (const p of points) {
  const [n, x, y, z] = p; if (z === 0) continue;
  const st = storyOf(z), lab = n;
  const vals = {};
  for (const c of ["DEAD", "LIVE", "EQX", "EQY"]) { vals[c] = shapes[c](x, y, z); jd.rows.push([st, lab, n, c, "LinStatic", "", "", s(vals[c][0]), s(vals[c][1]), s(vals[c][2])]); }
  modes.forEach(([, f], i) => { const d = f(x, y, z); jd.rows.push([st, lab, n, "Modal", "LinModEigen", "Mode", String(i + 1), s(d[0]), s(d[1]), s(d[2])]); });
  for (const [c, parts] of Object.entries(combos)) { const d = [0, 1, 2].map(k => parts.reduce((t, [q, f]) => t + vals[q][k] * f, 0)); jd.rows.push([st, lab, n, c, "Combination", "", "", s(d[0]), s(d[1]), s(d[2])]); }
}

// member forces
const caseList = ["DEAD", "LIVE", "EQX", "EQY", "ULS1", "ULS2", "ULS3"];
const ef = kind => ({name: "Element Forces - " + kind, fields: ["Story", "Frame", "UniqueName", "OutputCase", "CaseType", "Station", "P", "V2", "V3", "T", "M2", "M3"], rows: []});
const EB = ef("Beams"), EC = ef("Columns"), ED = ef("Braces");
const combine = (by, c) => combos[c] ? Object.fromEntries(["P", "V2", "V3", "T", "M2", "M3"].map(k => [k, combos[c].reduce((t, [q, f]) => t + by[q][k] * f, 0)])) : by[c];
const peak = new Map();
for (const f of frames) {
  const [name, kind, sec, st, x1, y1, z1, x2, y2, z2] = f, L = Math.hypot(x2 - x1, y2 - y1, z2 - z1), lvl = +st.slice(5), r = 1 - (lvl - 1) / N;
  const stations = kind === "beam" ? [0, 1, 2, 3, 4, 5, 6].map(k => L * k / 6) : [0, L / 2, L];
  const perim = kind === "beam" && (y1 === 0 && y2 === 0 || y1 === 30 && y2 === 30 || x1 === 0 && x2 === 0 || x1 === 48 && x2 === 48);
  const alongX = Math.abs(x2 - x1) > Math.abs(y2 - y1);
  let best = 0;
  for (const st0 of stations) {
    const by = {};
    for (const c of ["DEAD", "LIVE", "EQX", "EQY"]) {
      const o = {P: 0, V2: 0, V3: 0, T: 0, M2: 0, M3: 0}, x = st0;
      if (kind === "beam") {
        const w = (sec === "W24X76" ? 21 : sec === "W21X50" ? 15.5 : 17) * (c === "LIVE" ? 0.45 : c === "DEAD" ? 1 : 0) * (lvl === N ? 0.8 : 1);
        if (w) { o.V2 = w * (L / 2 - x); o.M3 = w * x * (L - x) / 2 - (perim ? w * L * L / 12 : 0); }
        const eq = (c === "EQX" && alongX || c === "EQY" && !alongX) && perim ? 300 * (1.12 - (lvl - 1) / N) : (c === "EQX" && alongX || c === "EQY" && !alongX) ? 40 * r : 0;
        if (eq) { o.M3 += eq * (1 - 2 * x / L); o.V2 += -2 * eq / L; }
        o.T = c === "DEAD" ? 0.6 * (x / L - 0.5) : 0;
      } else if (kind === "column") {
        const onX = X.indexOf(x1), onY = Y.indexOf(y1), edgeX = onX === 0 || onX === X.length - 1, edgeY = onY === 0 || onY === Y.length - 1, trib = edgeX && edgeY ? 15 : edgeX || edgeY ? 30 : 60;
        const above = N - lvl + 1, wq = c === "DEAD" ? 6.4 : c === "LIVE" ? 2.4 : 0;
        o.P = -wq * trib * above;
        if (c === "EQX") { const Mc = (edgeY ? 230 : 120) * (1.15 - (lvl - 1) / N); o.M3 = Mc * (1 - 2 * x / L); o.V2 = -2 * Mc / L; o.P += (edgeX ? (onX === 0 ? -1 : 1) * 32 * above : 0); }
        if (c === "EQY") { const Mc = (edgeX ? 210 : 110) * (1.15 - (lvl - 1) / N); o.M2 = Mc * (1 - 2 * x / L); o.V3 = -2 * Mc / L; o.P += (edgeY ? (onY === 0 ? -1 : 1) * 28 * above : 0); }
        if (c === "DEAD" || c === "LIVE") { const m = (edgeX || edgeY ? 18 : 6) * (c === "LIVE" ? 0.4 : 1); o.M3 = m * (1 - 2 * x / L) * (onX < 3 ? -1 : 1); o.V2 = -2 * m / L * (onX < 3 ? -1 : 1); }
      } else {
        const sign = x1 < x2 ? 1 : -1;
        o.P = c === "EQX" ? sign * 520 * (1.2 - lvl * 0.15) : c === "DEAD" ? -45 : c === "LIVE" ? -12 : 0;
        o.M3 = c === "DEAD" ? 1.8 * Math.sin(Math.PI * x / L) : 0;
      }
      by[c] = o;
    }
    for (const c of caseList) {
      const o = combine(by, c), table = kind === "beam" ? EB : kind === "column" ? EC : ED;
      table.rows.push([st, name, name, c, combos[c] ? "Combination" : "LinStatic", s(st0), s(o.P), s(o.V2), s(o.V3), s(o.T), s(o.M2), s(o.M3)]);
      if (combos[c]) best = Math.max(best, kind === "column" ? Math.abs(o.P) / 1 : Math.abs(o.M3));
    }
  }
  peak.set(name, best);
}

// reactions and drifts
const jr = {name: "Joint Reactions", fields: ["Story", "Label", "UniqueName", "OutputCase", "CaseType", "FX", "FY", "FZ", "MX", "MY", "MZ"], rows: []};
const colBase = new Map(); for (const row of EC.rows) if (row[0] === "Story1" && row[5] === "0") colBase.set(row[2] + "|" + row[3], row);
for (const f of frames.filter(f => f[1] === "column" && f[3] === "Story1")) {
  const n = at(f[4], f[5], 0);
  for (const c of caseList) { const row = colBase.get(f[0] + "|" + c); jr.rows.push(["Base", n, n, c, combos[c] ? "Combination" : "LinStatic", s(-row[7] * 1), s(-row[8] * 1), s(-row[6]), s(-row[10]), s(row[11]), "0"]); }
}
for (const p of base.filter(p => inCore(p[1], p[2]))) for (const c of caseList) { const k = c === "EQX" || c === "ULS2" ? 1 : 0, j = c === "EQY" || c === "ULS3" ? 1 : 0, g = c === "LIVE" ? 0.38 : c === "EQX" || c === "EQY" ? 0 : c.startsWith("ULS") ? 1.6 : 1; jr.rows.push(["Base", p[0], p[0], c, combos[c] ? "Combination" : "LinStatic", s(-k * 1650), s(-j * 1720), s(g * 4800 + (k + j) * (p[1] > xc ? 900 : -900)), s(j * 9000), s(-k * 9000), "0"]); }
const dr = {name: "Story Drifts", fields: ["Story", "OutputCase", "CaseType", "Direction", "Drift", "Label", "X", "Y", "Z"], rows: []};
for (const c of ["EQX", "EQY", "ULS2", "ULS3"]) for (let i = 1; i <= N; i++) {
  const z0 = stories[i - 1].elevation, z = stories[i].elevation, h = z - z0, f = c.startsWith("ULS") ? 1 : 1, sh = c === "EQX" || c === "ULS2" ? shapes.EQX : shapes.EQY;
  const u0 = sh(48, 30, z0), u1 = sh(48, 30, z);
  dr.rows.push([stories[i].name, c, combos[c] ? "Combination" : "LinStatic", "X", s(Math.abs(u1[0] - u0[0]) / h * f * 1e4 / 1e4), "", "48", "30", String(z)]);
  dr.rows.push([stories[i].name, c, combos[c] ? "Combination" : "LinStatic", "Y", s(Math.abs(u1[1] - u0[1]) / h), "", "48", "30", String(z)]);
}
for (const row of dr.rows) row[4] = String(Math.round(parseFloat(row[4]) * 1e6) / 1e6);
const mp = {name: "Modal Periods And Frequencies", fields: ["Case", "Mode", "Period", "Frequency", "CircFreq", "Eigenvalue"], rows: [...modes.map(([T], i) => ["Modal", String(i + 1), String(T), s(1 / T), s(2 * Math.PI / T), s((2 * Math.PI / T) ** 2)]), ["Modal", "4", "0.82", s(1 / 0.82), s(2 * Math.PI / 0.82), s((2 * Math.PI / 0.82) ** 2)], ["Modal", "5", "0.71", s(1 / 0.71), s(2 * Math.PI / 0.71), s((2 * Math.PI / 0.71) ** 2)], ["Modal", "6", "0.49", s(1 / 0.49), s(2 * Math.PI / 0.49), s((2 * Math.PI / 0.49) ** 2)]]};
const mm = {name: "Modal Participating Mass Ratios", fields: ["Case", "Mode", "Period", "UX", "UY", "UZ", "SumUX", "SumUY", "RZ"], rows: [["Modal", "1", "2.84", "0.0021", "0.6612", "0", "0.0021", "0.6612", "0.0034"], ["Modal", "2", "2.41", "0.6588", "0.0019", "0", "0.6609", "0.6631", "0.0011"], ["Modal", "3", "1.62", "0.0007", "0.0026", "0", "0.6616", "0.6657", "0.7012"], ["Modal", "4", "0.82", "0.0005", "0.1795", "0", "0.6621", "0.8452", "0.0008"], ["Modal", "5", "0.71", "0.1822", "0.0004", "0", "0.8443", "0.8456", "0.0002"], ["Modal", "6", "0.49", "0.0001", "0.0009", "0", "0.8444", "0.8465", "0.1798"]]};
const br = {name: "Base Reactions", fields: ["OutputCase", "CaseType", "FX", "FY", "FZ", "MX", "MY", "MZ"], rows: []};
for (const c of caseList) { let fx = 0, fy = 0, fz = 0; for (const row of jr.rows) if (row[3] === c) { fx += +row[5]; fy += +row[6]; fz += +row[7]; } br.rows.push([c, combos[c] ? "Combination" : "LinStatic", s(fx), s(fy), s(fz), s(fy * H * 0.6), s(-fx * H * 0.6), s(fx * 1.2)]); }

// design
const cap = {W24X76: 520, W21X50: 290, W16X31: 300, W14X257: 15800, W14X176: 10700, W14X109: 6500, W14X74: 3900, "HSS8X8X1/2": 1850};
const sd = {name: "Steel Frame Design Summary - AISC 360-16", fields: ["Story", "Label", "UniqueName", "Design Type", "Design Section", "Status", "PMM Combo", "PMM Ratio", "P Ratio", "M Major Ratio", "M Minor Ratio", "V Major Combo", "V Major Ratio"], rows: []};
let seed = 7; const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
for (const f of frames) {
  const [name, kind, sec, st] = f, pk = peak.get(name) || 0, lvl = +st.slice(5);
  let ratio = Math.min(1.12, pk / cap[sec] * (kind === "column" ? 1.05 + 0.25 * (lvl % 4) / 4 : kind === "brace" ? 2.2 : 1) * (0.92 + 0.16 * rnd()));
  if (kind === "beam" && sec === "W16X31" && lvl === 2 && rnd() < 0.5) ratio = 1.03 + 0.05 * rnd();
  const pr = kind === "column" ? ratio * 0.8 : 0.02, mr = kind === "beam" ? ratio * 0.97 : ratio * 0.22;
  sd.rows.push([st, name, name, kind === "column" ? "Column" : kind === "beam" ? "Beam" : "Brace", sec, ratio > 1 ? "Overstressed" : "No Messages", kind === "column" ? "ULS1" : "ULS2", s(ratio), s(pr), s(mr), s(ratio * 0.05), "ULS1", s(ratio * 0.31)]);
}

const sizes = {"W14X257": ["I/Wide Flange", 0.416, 0.406, 0.048, 0.0299], "W14X176": ["I/Wide Flange", 0.387, 0.398, 0.0334, 0.0211], "W14X109": ["I/Wide Flange", 0.364, 0.371, 0.0218, 0.0133], "W14X74": ["I/Wide Flange", 0.360, 0.257, 0.0199, 0.0114],
  "W24X76": ["I/Wide Flange", 0.607, 0.228, 0.0173, 0.0111], "W21X50": ["I/Wide Flange", 0.529, 0.166, 0.0136, 0.0097], "W16X31": ["I/Wide Flange", 0.403, 0.140, 0.0112, 0.007], "HSS8X8X1/2": ["Box/Tube", 0.203, 0.203, 0.0118, 0.0118]};
const pack = {
  manifest: {format: "cpmx", version: 1, model: "Sample office tower", program: "ETABS", etabs: "23.2.0", units: {force: "kN", length: "m", temperature: "C"}, analysis_run: true, created: "2026-11-25T08:00:00Z", by: "sample data, not a real analysis", made_with: "CSI ProMax"},
  model: {
    stories, frames, areas, points, grids: [...X.map((v, i) => [XL[i], "X", v]), ...Y.map((v, i) => [YL[i], "Y", v])], frame_sizes: sizes, area_sizes: {Deck150: 0.15, Wall350: 0.35, Wall400: 0.4},
    section_materials: {...Object.fromEntries(Object.keys(sizes).filter(k => k[0] === "W").map(k => [k, "A992Fy50"])), "HSS8X8X1/2": "A500GrC", Deck150: "C30", Wall350: "C40", Wall400: "C40"},
    supports, frame_sections: Object.keys(sizes), area_sections: ["Deck150", "Wall350", "Wall400"], materials: ["A992Fy50", "A500GrC", "C30", "C40"],
    load_patterns: [{name: "DEAD", type: "Dead", self_weight: 1}, {name: "LIVE", type: "Live", self_weight: 0}, {name: "EQX", type: "Seismic", self_weight: 0}, {name: "EQY", type: "Seismic", self_weight: 0}],
    load_cases: ["DEAD", "LIVE", "EQX", "EQY", "Modal"], combinations: Object.keys(combos),
  },
  checks: {note: "Checks made from the tables in this pack. Sample data.", checks: {
    drift: {status: "pass", limit: 0.0025, largest: Math.max(...dr.rows.map(r => +r[4])), at: dr.rows.reduce((a, r) => +r[4] > +a[4] ? r : a)[0]},
    steel_design: {status: sd.rows.some(r => +r[7] > 1) ? "fail" : "pass", members: sd.rows.length, over_one: sd.rows.filter(r => +r[7] > 1).map(r => ({member: r[2], story: r[0], section: r[4], ratio: +r[7]}))},
    modal_mass: {status: "pass", sum_ux: 0.8444, sum_uy: 0.8465, note: "90% is reached with more modes than this sample carries."},
  }},
  comments: [{id: "s1", by: "Reviewer", at: "2026-11-25T09:10:00Z", text: "This filler beam is over 1.0 at Story2. Is the deck span right here?", target: {kind: "frame", what: "beam", name: sd.rows.find(r => +r[7] > 1 && r[4] === "W16X31")?.[2] || "B1", story: "Story2"}}],
  tables: {},
};
for (const t of [jd, EB, EC, ED, jr, dr, mp, mm, br, sd]) pack.tables[t.name] = t;
const json = JSON.stringify(pack);
const html = fs.readFileSync(viewerPath, "utf8"), slot = '<script id="pack" type="application/json"></script>';
if (!html.includes(slot)) throw new Error("no pack slot in the viewer");
fs.writeFileSync(outHtml, html.replace(slot, '<script id="pack" type="application/json">' + json.replace(/<\//g, "<\\/").replace(/<!--/g, "<\\u0021--") + "</script>"));
if (outJson) fs.writeFileSync(outJson, json);
console.log(`frames ${frames.length}, areas ${areas.length}, points ${points.length}, rows ${Object.values(pack.tables).reduce((n, t) => n + t.rows.length, 0)}, ${(json.length / 1e6).toFixed(1)} MB`);
