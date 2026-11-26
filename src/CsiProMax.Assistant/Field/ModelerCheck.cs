using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Bridge;
using CsiProMax.Model;

namespace CsiProMax.Assistant;

/// <summary>
/// The Modeler's window, checked as an engineer uses it: the live page is served the way the Modeler serves it (the
/// same server, secret address and edit route, with HandModel behind it) on a scratch model of its own, and opened in a
/// browser without a window. A script on the page fills and applies a Define form, draws a column by a click and undoes
/// it, then runs every item of the File, Assign, Define, Draw and Edit menus: an item ending in "..." must open its form
/// and close it again, and no item may make the page report an error. It runs with the AI chat on and off, so modelling
/// without the AI is checked on the page too. The user's own model and its undo history are never touched.
/// </summary>
public static class ModelerCheck
{
    /// <summary>The scratch model: two bays, one way, two stories, built by the Modeler's own template.</summary>
    public static StructuralModel Scratch() => Templates.ConcreteFrame("CPM Modeler window check", [6, 6], [5], 2, 3.0);

    private static readonly string[] ModelerTables = ["Grid Definitions", "Frame Assignments - Section Properties", "Area Assignments - Section Properties", "Model Checks", "Quantities", .. OfflineEtabs.LoadTables];

    public const string Script = """
    (function () {
      const CHAT = __CHAT__;
      const errors = [];
      window.addEventListener("error", e => errors.push(String(e.message || e.error || e)));
      window.addEventListener("unhandledrejection", e => errors.push("promise: " + String(e.reason && e.reason.message || e.reason)));
      const wait = ms => new Promise(r => setTimeout(r, ms));
      const items = [];
      const add = (name, ok, detail, skipped) => items.push({name, ok: !!ok, detail: String(detail == null ? "" : detail).slice(0, 400), skipped: !!skipped});
      const dialogsOpen = () => document.querySelectorAll(".dlg").length;
      const closeDialogs = () => document.querySelectorAll(".dlg").forEach(d => { const x = d.querySelector(".dt .x"); if (x) x.click(); else d.remove(); });
      async function step(name, body) {
        const before = errors.length;
        try {
          const said = await body();
          const fresh = errors.slice(before);
          if (said && said.skipped) add(name, true, said.skipped, true);
          else if (fresh.length) add(name, false, "the page reported: " + fresh.join("; "));
          else if (said && said.fail) add(name, false, said.fail);
          else add(name, true, said && said.ok || "");
        } catch (e) { add(name, false, "it threw: " + (e && e.message || e)); }
        try { closeDialogs(); hideMenus(); if (typeof tool === "string" && tool !== "select") act("t:select"); } catch (e) { }
        await wait(60);
      }
      // The items that would ask the PC for a file or a printer are run another way (Ahmed 2026-11-26: nothing left
      // out but a program that is not installed): File > Open opens a file the check saved, typed in the form as an
      // engineer does; Import > Tables from Excel is handed a workbook the check wrote, through the form's own file box
      // (as if picked, with no picker); Print runs from the menu with the printer dialog stubbed, and the window it
      // prints must be drawn.
      const FOLDER = __FOLDER__;
      const formOk = label => { const forms = document.querySelectorAll(".dlg"), f = forms[forms.length - 1]; const b = f && [...f.querySelectorAll(".df button")].find(x => x.textContent === label); if (!b) throw new Error("the form has no " + label + " button"); b.click(); };
      const formErr = () => { const e = document.querySelector(".dlg .ferr:not([hidden])"); return e ? e.textContent : ""; };
      // The Engine's results on the model: a small pack of the open model with one result table, read as a dropped file
      // would be (the file picker needs a person's click). The run itself is the field test's Engine run step, never
      // started from here, as it would start the CSI program.
      const engineFile = () => new File([JSON.stringify({manifest: {}, model: P.model, tables: {"Joint Displacements": {name: "Joint Displacements", fields: ["Label", "Story", "OutputCase", "Ux", "Uy", "Uz"], rows: [["CPM-CHK", "", "Dead", "0.001", "0", "0"]]}}})], "CPM engine check.cpmx", {type: "application/json"});
      const engineIn = async () => {
        await engineImport(engineFile()); await wait(600);
        if (!engine.res || !P.tables["Joint Displacements"]) return "the results were not put on the model: " + document.getElementById("hint").textContent;
        return "";
      };
      const engineImportCheck = async () => {
        const bad = await engineIn(); if (bad) return {fail: bad};
        engineClear(); await wait(400);
        return {ok: "a pack's results were shown on the model, then cleared"};
      };
      const OTHER_WAY = {
        "eng:import": engineImportCheck,
        "eng:clear": async () => {
          if (!engine.res) { const bad = await engineIn(); if (bad) return {fail: bad}; }
          if (offNow("eng:clear")) return {fail: "greyed out, although results are on the model"};
          await act("eng:clear"); await wait(400);
          return engine.res || P.tables["Joint Displacements"] ? {fail: "the results are still on the model"} : {ok: "the results were cleared; the model is unlocked"};
        },
        "eng:runDesign": async () => OTHER_WAY["eng:run"](),
        "eng:run": async () => {
          const r = await fetch("run", {method: "POST", headers: {"Content-Type": "application/json", "X-CPM-View": "1"}, body: JSON.stringify({status: true}), cache: "no-store"}).then(x => x.json()).catch(e => ({error: e.message}));
          return r.running !== undefined || r.ok !== undefined ? {ok: "the run answers (" + (r.running ? "running" : "idle") + "); the run itself is the Engine run step"} : {fail: "the run does not answer: " + (r.error || JSON.stringify(r))};
        },
        "m:open": async () => {
          const saved = await hmRun("save_model_as", {name: "CPM open check", folder: FOLDER}, true);
          if (!saved.ok) return {fail: "the check could not save a model to open: " + saved.error};
          const file = (saved.result.modeler_file || "").endsWith(".cpm") ? saved.result.modeler_file : saved.result.e2k || saved.result.file;
          const count = frames.length;
          await act("m:open"); await wait(600);
          const box = document.querySelector(".dlg #oP");
          if (!box) return {fail: "the Open form did not open"};
          box.value = file; formOk("Open"); await wait(1500);
          const err = formErr();
          for (let i = 0; i < 20 && hm.state && hm.state.file !== file && window.cpmPull; i++) { await window.cpmPull(); await wait(150); }
          if (err) return {fail: "the file did not open: " + err};
          if (frames.length !== count) return {fail: `the opened model has ${frames.length} frames, not ${count}`};
          return {ok: "opened " + file.split(/[\\/]/).pop() + ` (${count} frames)`};
        },
        "m:xlsxIn": async () => {
          const made = await hmRun("export_tables_excel", {name: "CPM import check", folder: FOLDER}, true);
          if (!made.ok || !made.result.saved) return {fail: "the check could not write a workbook: " + made.error};
          const name = made.result.saved.split(/[\\/]/).pop();
          const r = await fetch("check-file?name=" + encodeURIComponent(name), {cache: "no-store"});
          if (!r.ok) return {fail: "the check could not read back its workbook " + name};
          const file = new File([await r.blob()], name, {type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"});
          await act("m:xlsxIn"); await wait(600);
          const input = document.querySelector(".dlg #iF");
          if (!input) return {fail: "the Import form did not open"};
          const dt = new DataTransfer(); dt.items.add(file); input.files = dt.files;
          input.dispatchEvent(new Event("change", {bubbles: true}));
          const before = (await hmDefs()).frame_sections.length;
          formOk("Import");
          let said = "";
          for (let i = 0; i < 40 && !/^Imported/.test(said); i++) { await wait(150); said = document.getElementById("hint").textContent; if (formErr()) break; }
          const err = formErr();
          if (err || !/^Imported/.test(said)) return {fail: "the workbook was not imported: " + (err || said || hm.lastError)};
          const after = (await hmDefs()).frame_sections.length;
          if (after !== before) return {fail: `reading back the model's own tables changed the frame sections from ${before} to ${after}`};
          await hmRun("undo"); await wait(600);
          return {ok: said.slice(0, 200)};
        },
        "print": async () => {
          const real = window.print; let asked = 0, drawn = "";
          window.print = () => { asked++; const p = panes[active], c = p && p.hold && p.hold.querySelector("canvas"); drawn = c ? `${c.width} x ${c.height}` : ""; };
          try {
            const file = document.querySelector('.menu[data-menu="File"]'); hideMenus(); file.click(); await wait(200);
            const item = file.querySelector('[data-do="print"]');
            if (!item) return {fail: "File has no Print item"};
            item.click(); await wait(300);
          } finally { window.print = real; }
          if (asked !== 1) return {fail: `Print asked for the printer ${asked} times, not once`};
          if (!drawn || /^0 x|x 0$/.test(drawn)) return {fail: "the window to print has no drawing: " + (drawn || "no canvas")};
          return {ok: "the printer was asked for the active window, drawn at " + drawn};
        },
      };
      function flat(list, path, out) {
        for (const it of list || []) {
          if (!it) continue;
          if (Array.isArray(it[1])) flat(it[1], path + " > " + it[0], out);
          else out.push({path: path + " > " + it[0], label: it[0], act: it[1]});
        }
        return out;
      }
      // Whether an item is greyed out now (the menus are made again each time they open).
      function offNow(a) {
        const all = []; const m = (typeof forProgram === "function" ? forProgram : x => x)(modelerMenus(viewerMenus()));
        for (const k of Object.keys(m)) (function walk(list) { for (const it of list || []) { if (!it) continue; if (Array.isArray(it[1])) walk(it[1]); else all.push(it); } })(m[k]);
        const hit = all.find(it => it[1] === a);
        return !hit || !!hit[3];
      }
      async function run() {
        await wait(1500);
        await step("the Modeler page opens live on the scratch model", async () => {
          if (!window.cpmLive) return {fail: "the page is not in live mode (no Draw, Assign or Define by hand)"};
          if (errors.length) return {fail: "errors while opening: " + errors.join("; ")};
          return frames.length > 0 ? {ok: `${frames.length} frames, ${areas.length} areas`} : {fail: "the page shows no frames"};
        });
        await step(CHAT ? "the chat tab is there (AI chat on)" : "no chat tab (AI chat off)", async () => {
          await wait(400);
          const b = document.querySelector('[data-tab="chat"]'), shown = !!b && !b.hidden;
          return shown === CHAT ? {ok: shown ? "shown" : "hidden"} : {fail: shown ? "the chat tab shows with the AI off" : "the chat tab is missing with the AI on"};
        });
        await step("Define > Frame Sections: a section is added through the form", async () => {
          await act("m:frameSections"); await wait(500);
          const addNew = document.querySelector('.dlg button[data-a="0"]');
          if (!addNew) return {fail: "the Frame Sections form has no Add button"};
          addNew.click(); await wait(1000);
          const set = (sel, v) => { const e = document.querySelector(sel); if (!e) throw new Error("the form has no " + sel); e.value = v; e.dispatchEvent(new Event("input", {bubbles: true})); e.dispatchEvent(new Event("change", {bubbles: true})); };
          set("#sN", "CPM-CHK450"); set('#sDims [data-k="D"]', "0.45"); set('#sDims [data-k="B"]', "0.3");
          const forms = document.querySelectorAll(".dlg"); forms[forms.length - 1].querySelector(".df button.primary").click();
          await wait(1500);
          const defs = await hmDefs();
          return (defs.frame_sections || []).some(s => s.name === "CPM-CHK450") ? {ok: "CPM-CHK450 is in the model"} : {fail: "the section did not reach the model: " + (hm.lastError || document.getElementById("hint").textContent)};
        });
        await step("Define > Material Properties: a concrete with its design, nonlinear, damping, time dependent and notes forms filled, and undo takes it out", async () => {
          await act("m:materials"); await wait(500);
          const addNew = document.querySelector('.dlg button[data-a="0"]');
          if (!addNew) return {fail: "the Define Materials form has no Add button"};
          addNew.click(); await wait(1000);
          const top = () => { const f = document.querySelectorAll(".dlg"); return f[f.length - 1]; };
          const set = (root, sel, v) => { const e = root.querySelector(sel); if (!e) throw new Error("the form has no " + sel); if (e.type === "checkbox" || e.type === "radio") e.checked = v; else e.value = v; e.dispatchEvent(new Event("input", {bubbles: true})); e.dispatchEvent(new Event("change", {bubbles: true})); };
          const main = top();
          set(main, "#mN", "CPM-CHKMAT"); set(main, "#mC", "Yellow");
          for (const [btn, fill] of [
            ["#mDes", f => { set(f, '[data-v="FC"]', "40000"); set(f, "#dLw", true); set(f, "#dLf", "0.85"); }],
            ["#mNl", f => { set(f, "#nH", "Takeda"); set(f, '[data-v="STRAINATULTIMATE"]', "0.0035"); }],
            ["#mDamp", f => set(f, '[data-k="ModalRatio"]', "0.03")],
            ["#mTime", f => { set(f, "#tT", "CEB-FIP 90"); set(f, "#tC", true); set(f, '#tP [data-k="RelHumid"]', "70"); }],
            ["#mNotes", f => set(f, "#nT", "check notes")],
          ]) {
            main.querySelector(btn).click(); await wait(500);
            const f = top(); if (f === main) return {fail: btn + " opened no form"};
            fill(f); f.querySelector(".df button.primary").click(); await wait(300);
          }
          set(main, 'input[name="mRup"][value="Yes"]', true); set(main, "#mFr", "4500");
          main.querySelector(".df button.primary").click(); await wait(1500);
          const pd = (await hmRun("property_definitions", {}, true)).result, m = pd.materials.find(x => x.name === "CPM-CHKMAT");
          if (!m) return {fail: "the material did not reach the model: " + (hm.lastError || (main.querySelector(".ferr") || {}).textContent)};
          const t = m.tables || {}, one = k => (t[k] || [{}])[0];
          const got = {fc: m.values.FC, hys: m.texts.HYSTYPE, ult: m.values.STRAINATULTIMATE, damp: one("Damping Parameters").ModalRatio, lw: one("Concrete Data").LtWtConc, fr: one("Concrete Data").UserFr, time: one("Concrete Data").TimeType, rh: one("Time Dependence - CEB-FIP 90").RelHumid, notes: one("General").Notes, color: one("General").Color};
          const want = {fc: 40000, hys: "Takeda", ult: 0.0035, damp: "0.03", lw: "Yes", fr: "4500", time: "CEB-FIP 90", rh: "70", notes: "check notes", color: "Yellow"};
          const off = Object.keys(want).filter(k => String(got[k]) !== String(want[k]));
          closeDialogs(); await wait(150);
          if (off.length) return {fail: "not as filled in: " + off.map(k => `${k} ${got[k]} (wanted ${want[k]})`).join(", ")};
          await hmRun("undo"); await wait(800);
          const back = ((await hmRun("property_definitions", {}, true)).result.materials || []).some(x => x.name === "CPM-CHKMAT");
          return back ? {fail: "undo did not take the material out"} : {ok: "every sub-form reached the model, then undone"};
        });
        await step("Assign > Frame > Releases: a beam pinned at both ends through the form, and undo takes it off", async () => {
          const b = frames.findIndex(f => f.kind === "beam");
          if (b < 0) return {fail: "the model has no beam"};
          sel.clear(); sel.add(b + 1); selChanged();
          await act("m:ad:releases"); await wait(900);
          const i = document.querySelector('.dlg [data-i="M3"]'), j = document.querySelector('.dlg [data-j="M3"]');
          if (!i || !j) return {fail: "the releases form did not open"};
          i.checked = true; j.checked = true;
          const forms = document.querySelectorAll(".dlg"); forms[forms.length - 1].querySelector(".df button.primary").click();
          await wait(1200);
          const name = frames[b].name, pd = await hmRun("property_definitions", {objects: [name]}, true);
          const o = pd.ok && pd.result.objects && pd.result.objects[0];
          closeDialogs(); await wait(150);
          if (!o || !(o.release_i || []).includes("M3") || !(o.release_j || []).includes("M3")) return {fail: "the releases did not reach the beam: " + (hm.lastError || JSON.stringify(o))};
          await hmRun("undo"); await wait(800);
          const back = await hmRun("property_definitions", {objects: [name]}, true);
          return (back.result.objects[0].release_i || []).length ? {fail: "undo did not take the releases off"} : {ok: "M3 released at both ends of " + name + ", then undone"};
        });
        await step("Assign > Frame > Hinges: auto hinges at both ends of a beam through the form, and undo takes them off", async () => {
          const b = frames.findIndex(f => f.kind === "beam");
          if (b < 0) return {fail: "the model has no beam"};
          sel.clear(); sel.add(b + 1); selChanged();
          await act("m:ad:hinges"); await wait(900);
          const ends = document.querySelector(".dlg #hEnds");
          if (!ends) return {fail: "the hinges form did not open"};
          ends.click(); await wait(150);
          const forms = document.querySelectorAll(".dlg"); forms[forms.length - 1].querySelector(".df button.primary").click();
          await wait(1200);
          const name = frames[b].name, pd = await hmRun("property_definitions", {objects: [name]}, true);
          const o = pd.ok && pd.result.objects && pd.result.objects[0];
          closeDialogs(); await wait(150);
          if (!o || (o.hinges || []).length < 2) return {fail: "the hinges did not reach the beam: " + (hm.lastError || JSON.stringify(o))};
          await hmRun("undo"); await wait(800);
          const back = await hmRun("property_definitions", {objects: [name]}, true);
          return (back.result.objects[0].hinges || []).length ? {fail: "undo did not take the hinges off"} : {ok: "hinges at 0.05 and 0.95 of " + name + ", then undone"};
        });
        await step("Assign > Joint > Springs: a point spring goes on the joints of a selected beam, and undo takes it off", async () => {
          const made = await hmRun("define_spring", {kind: "point", name: "CPM-CHKPS", values: {UZ: 1000}}, true);
          if (!made.ok) return {fail: "the spring was not defined: " + made.error};
          const b = frames.findIndex(f => f.kind === "beam");
          sel.clear(); sel.add(b + 1); selChanged();
          await act("m:ad:joint"); await wait(900);
          const pick = document.querySelector(".dlg #xS");
          if (!pick) { closeDialogs(); return {fail: "the joint springs form did not open: " + (hm.lastError || "")}; }
          pick.value = "CPM-CHKPS";
          const forms = document.querySelectorAll(".dlg"); forms[forms.length - 1].querySelector(".df button.primary").click();
          await wait(1200);
          const err = document.querySelector(".dlg .ferr:not([hidden])");
          const pd = await hmRun("property_definitions", {}, true), sp = pd.result.springs.find(x => x.name === "CPM-CHKPS");
          closeDialogs(); await wait(150);
          if (!sp || sp.used_by < 2) return {fail: "the spring is not on the beam's two joints: " + (err ? err.textContent : hm.lastError) + " " + JSON.stringify(sp)};
          await hmRun("undo"); await wait(800); await hmRun("undo"); await wait(800);
          const back = (await hmRun("property_definitions", {}, true)).result.springs.some(x => x.name === "CPM-CHKPS");
          return back ? {fail: "two undos did not take the spring away"} : {ok: `on ${sp.used_by} joints, then undone`};
        });
        await step("Assign > Assign Objects to Group: a selected beam goes into a new group, and undo takes it out", async () => {
          const b = frames.findIndex(f => f.kind === "beam");
          sel.clear(); sel.add(b + 1); selChanged();
          await act("m:sa:group"); await wait(900);
          const box = document.querySelector(".dlg #zNew");
          if (!box) { closeDialogs(); return {fail: "the group form did not open"}; }
          box.value = "CPM-CHKG";
          const forms = document.querySelectorAll(".dlg"); forms[forms.length - 1].querySelector(".df button.primary").click();
          await wait(1200);
          const g = ((await hmRun("structure_definitions", {}, true)).result.groups || []).find(x => x.name === "CPM-CHKG");
          closeDialogs(); await wait(150);
          if (!g || g.members !== 1) return {fail: "the beam is not in CPM-CHKG: " + (hm.lastError || JSON.stringify(g))};
          await hmRun("undo"); await wait(800);
          const back = ((await hmRun("structure_definitions", {}, true)).result.groups || []).some(x => x.name === "CPM-CHKG");
          return back ? {fail: "undo did not take the group away"} : {ok: "in the group, then undone"};
        });
        await step("Draw > Draw Columns: a column is drawn by a click, and Edit > Undo takes it back", async () => {
          // The plan's view matrix is set when the pane is drawn, and a headless page does not always draw between steps
          // (CI, and 2 runs in 9 here): it is drawn on purpose, and the matrix taken from the pane's own size if need be.
          const p = panes.find(x => x.mode === "plan");
          if (!p) return {fail: "no plan view is open"};
          active = panes.indexOf(p); draw();
          for (let i = 0; i < 20 && !(p.M && p.M.to); i++) await wait(100);
          if (!(p.M && p.M.to)) { const r = p.hold.getBoundingClientRect(); if (r.width < 4 || r.height < 4) return {fail: `the plan view has no room (${r.width} x ${r.height})`}; p.M = matrices(p, r.width, r.height); }
          const before = frames.length;
          act("d:column"); await wait(200);
          const z = elevationOf(p.story) ?? 0, s = p.M.to([3, 2.5, z]);
          await drawClick(p, s[0], s[1], false); await wait(1500);
          if (frames.length !== before + 1) return {fail: `the click made ${frames.length - before} frames, not 1: ` + document.getElementById("hint").textContent};
          act("t:select");
          await act("m:undo"); await wait(1500);
          return frames.length === before ? {ok: "drawn, then undone"} : {fail: `after Undo there are ${frames.length} frames, not ${before}`};
        });
        await step("Analyze > Check Model for Instabilities: the checker behind the window answers, and lists nothing wrong in the sound model", async () => {
          await act("inst"); await wait(2500);
          if (!inst.answer) return {fail: "no answer came: " + (document.querySelector("#instHead") || {}).textContent};
          if (!inst.answer.ok) return {fail: "the check failed: " + inst.answer.error};
          if (inst.answer.errors) return {fail: "the sound scratch model has errors: " + inst.answer.findings.slice(0, 3).map(f => f.message).join(" | ")};
          if (!document.querySelector("#instList")) return {fail: "the checker's window did not open"};
          const local = instLocal();
          if (local.errors) return {fail: "the page's light check finds errors in the sound model: " + local.findings[0].message};
          return {ok: inst.answer.summary};
        });
        await step("Analyze: a finding selects its objects and rings its places", async () => {
          inst.answer = {ok: true, errors: 1, warnings: 0, findings: [{level: "error", check: "floating", message: "test", fix: "test", objects: [{kind: "frame", name: frames[0].name}], at: [frames[0].a]}], notes: []};
          await act("inst"); await wait(300);
          inst.answer = {ok: true, errors: 1, warnings: 0, findings: [{level: "error", check: "floating", message: "test", fix: "test", objects: [{kind: "frame", name: frames[0].name}], at: [frames[0].a]}], notes: []};
          instList(); instGo(0); await wait(300);
          return sel.has(frames[0].id) && inst.cur === 0 ? {ok: "selected and ringed"} : {fail: "the finding's frame was not selected"};
        });
        await step("Define > Load Patterns > Modify Lateral Load: a seismic pattern takes ASCE 7-16, and its story forces show", async () => {
          const made = await hmRun("define_load_pattern", {name: "CPM-CHKEQ", type: "Seismic", self_weight: 0}, true);
          if (!made.ok) return {fail: "the seismic pattern was not added: " + made.error};
          await act("m:patterns"); await wait(600);
          const row = [...document.querySelectorAll(".dlg #pL tr")].find(tr => tr.textContent.includes("CPM-CHKEQ"));
          if (!row) return {fail: "CPM-CHKEQ is not in the Load Patterns form"};
          row.click(); await wait(200);
          const lat = document.querySelector(".dlg #pLat");
          if (!lat || lat.disabled) return {fail: "Modify Lateral Load Pattern is not there, or greyed out, for a seismic pattern"};
          lat.click(); await wait(1200);
          const code = document.querySelector(".dlg #lCode");
          if (!code) return {fail: "the lateral load form did not open"};
          code.value = "ASCE 7-16"; code.dispatchEvent(new Event("change", {bubbles: true})); await wait(200);
          const forms = document.querySelectorAll(".dlg"); forms[forms.length - 1].querySelector(".df button.primary").click();
          await wait(2000);
          const defs = await hmDefs(), p = (defs.load_patterns || []).find(x => x.name === "CPM-CHKEQ");
          closeDialogs(); await wait(150);
          await hmRun("undo"); await wait(800); await hmRun("undo"); await wait(800);
          const back = ((await hmDefs()).load_patterns || []).some(x => x.name === "CPM-CHKEQ");
          if (!p || !p.lateral || p.lateral.code !== "ASCE 7-16") return {fail: "the code did not reach the pattern: " + (hm.lastError || JSON.stringify(p && p.lateral))};
          if (p.lateral.error || !(p.lateral.story_forces || []).some(f => f.fx > 0)) return {fail: "no story forces: " + (p.lateral.error || "")};
          return back ? {fail: "two undos did not take the pattern out again"} : {ok: `V = ${p.lateral.results.V} on ${p.lateral.story_forces.length} stories, then undone`};
        });
        await step("Edit > Model Text: a combination written in the program's own text goes into the model, and undo takes it out", async () => {
          await act("m:text"); await wait(900);
          const sec = document.querySelector(".dlg #tS"), box = document.querySelector(".dlg #tX");
          if (!sec || !box) return {fail: "the Model Text form did not open"};
          document.querySelector(".dlg #tNew").value = "LOAD COMBINATIONS"; document.querySelector(".dlg #tNew").dispatchEvent(new Event("change", {bubbles: true})); await wait(900);
          box.value = (box.value ? box.value + "\n" : "") + '  COMBO "CPM-CHKTXT"  TYPE "Linear Add"\n  COMBO "CPM-CHKTXT"  LOADCASE "Dead"  SF 1.4';
          const ok = [...document.querySelectorAll(".dlg .df button")].find(b => b.textContent === "OK"); ok.click(); await wait(1500);
          const err = document.querySelector(".dlg .ferr:not([hidden])");
          const has = ((await hmDefs()).combinations || []).some(c => c.name === "CPM-CHKTXT");
          closeDialogs(); await wait(150);
          if (!has) return {fail: "the combination is not in the model: " + (err ? err.textContent : hm.lastError)};
          await hmRun("undo"); await wait(800);
          const back = ((await hmDefs()).combinations || []).some(c => c.name === "CPM-CHKTXT");
          return back ? {fail: "undo did not take it out"} : {ok: "taken, then undone"};
        });
        // A beam and a floor selected before each item, so the items that act on a selection are on (Delete empties it).
        const pick = () => { try { sel.clear(); const b = frames.findIndex(f => f.kind === "beam"); if (b >= 0) sel.add(b + 1); if (areas.length) sel.add(frames.length + 1); selChanged(); } catch (e) { } };
        const programMenus = () => (typeof forProgram === "function" ? forProgram : x => x)(modelerMenus(viewerMenus()));
        const listNow = () => { const m = programMenus(), out = []; for (const name of ["File", "Assign", "Define", "Draw", "Edit", "Analyze"]) flat(m[name], name, out); return out; };
        const list = listNow();
        // The menus follow the program the model is for (Ahmed 2026-11-26): the items only SAP2000 or SAFE has are run
        // with the model made for that program, after the first program's.
        const seen = new Set(list.map(it => it.act));
        for (const target of typeof forProgram === "function" ? ["sap2000", "safe"] : []) {
          let extra = [];
          await step(`the menus follow the program: the model made for ${target}`, async () => {
            const j = await hmRun("set_model_program", {program: target}, true);
            if (!j.ok) return {fail: "set_model_program: " + j.error};
            for (let i = 0; i < 30 && program !== target; i++) { if (window.cpmPull) await window.cpmPull(); await wait(200); }
            if (program !== target) return {fail: `the page still shows ${program}`};
            const now = listNow(), acts = now.map(it => it.act);
            const wrong = target === "safe" ? acts.filter(x => ["m:st:pier", "m:ex:lanes", "m:ex:constraints", "m:ad:hinges"].includes(x)) : acts.filter(x => ["m:st:pier", "m:ex:strips"].includes(x));
            if (wrong.length) return {fail: "it offers what " + target + " does not have: " + wrong.join(", ")};
            extra = now.filter(it => !seen.has(it.act)); extra.forEach(it => seen.add(it.act));
            return {ok: `${now.length} items, ${extra.length} only here`};
          });
          list.push(...extra.map(it => ({...it, path: it.path + " (" + target + ")", target})));
        }
        if (typeof forProgram === "function") { await hmRun("set_model_program", {program: "etabs"}, true); if (window.cpmPull) await window.cpmPull(); await wait(400); }
        for (const it of list) {
          await step("menu: " + it.path, async () => {
            if (it.target && program !== it.target) { await hmRun("set_model_program", {program: it.target}, true); for (let i = 0; i < 30 && program !== it.target; i++) { if (window.cpmPull) await window.cpmPull(); await wait(200); } }
            pick();
            if (it.act === "m:redo" && offNow(it.act)) { await hmRun("define_load_pattern", {name: "CPM-CHKREDO", type: "Other", self_weight: 0}, true); await hmRun("undo"); await wait(400); }
            if (offNow(it.act) && it.act !== "eng:clear") return {fail: "greyed out, although the check selected a beam and a floor for it first"};
            if (OTHER_WAY[it.act]) return await OTHER_WAY[it.act](it);
            const before = dialogsOpen();
            await act(it.act); await wait(700);
            if (it.label.endsWith("...")) {
              if (dialogsOpen() <= before) return {fail: "its form did not open: " + document.getElementById("hint").textContent};
              closeDialogs(); await wait(150);
              return dialogsOpen() === 0 ? {ok: "its form opened and closed"} : {fail: "its form did not close"};
            }
            return {ok: document.getElementById("hint").textContent.slice(0, 200)};
          });
        }
        add("no errors on the page during the whole check", errors.length === 0, errors.slice(0, 5).join("; "));
      }
      run().catch(e => add("the check itself", false, String(e && e.message || e))).finally(() => {
        const out = document.createElement("script"); out.type = "application/json"; out.id = "cpm-selfcheck";
        out.textContent = JSON.stringify({items}).replace(/</g, "\\u003c");
        document.body.appendChild(out); document.title = "cpm-selfcheck-done";
      });
    })();
    """;

    private static string PageScript(bool chat, string folder) =>
        Script.Replace("__CHAT__", chat ? "true" : "false", StringComparison.Ordinal)
              .Replace("__FOLDER__", JsonSerializer.Serialize(folder), StringComparison.Ordinal);

    /// <summary>
    /// Serves the live Modeler page on a scratch model and checks it in <paramref name="browser"/> (Edge or Chrome when
    /// null). <paramref name="chat"/>: the page as a window with the AI chat on serves it (a chat with nothing in it), or off.
    /// </summary>
    public static ViewerCheck.Result Run(bool chat, string folder, string? browser = null, int seconds = 240)
    {
        browser ??= ViewerCheck.FindBrowser();
        if (browser is null) return new ViewerCheck.Result([], [], "no Edge or Chrome was found on this PC to open the Modeler's window in");
        Directory.CreateDirectory(folder);
        // Where the page saves the model it opens again and the workbook it imports (File > Open, Import > Excel).
        var files = Path.Combine(folder, "files " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(files);
        var link = new OfflineEtabs(Scratch());
        var gate = new object();
        var endpoint = new McpEndpoint("CSI ProMax Modeler check", "1", () => "", _ => [], (_, _, _, _) => throw new InvalidOperationException("no tools here"));
        using var server = new McpHttpServer(endpoint);
        server.ViewHandler = (path, query) =>
        {
            lock (gate)
            {
                var content = Pack.Build(link, ModelerTables, [], [], null, null).Content;
                switch (path)
                {
                    case "":
                        var html = Pack.LiveViewerHtml(content, link.Revision, chat).Replace("</body>", "<script>" + PageScript(chat, files) + "</script></body>", StringComparison.Ordinal);
                        return (Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
                    case "model":
                        if (query == "rev=" + link.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) return ([], "application/json");
                        return (Encoding.UTF8.GetBytes(new JsonObject { ["rev"] = link.Revision, ["pack"] = content }.ToJsonString()), "application/json");
                    case "check-file":
                        // Only a file the check itself wrote in its own folder: the page reads back its workbook.
                        var asked = Uri.UnescapeDataString(query.StartsWith("name=", StringComparison.Ordinal) ? query[5..] : "");
                        var file = Path.Combine(files, Path.GetFileName(asked));
                        return asked.Length > 0 && File.Exists(file) ? (File.ReadAllBytes(file), "application/octet-stream") : null;
                    default:
                        return null;
                }
            }
        };
        server.ViewPost = (path, body) =>
        {
            (byte[], string)? answer = null;
            if (path == "edit")
                lock (gate)
                {
                    using var request = JsonDocument.Parse(body);
                    answer = (Encoding.UTF8.GetBytes(HandModel.Handle(link, request.RootElement.Clone()).ToJsonString()), "application/json");
                }
            else if (path == "check")
                lock (gate) answer = (Encoding.UTF8.GetBytes(StabilityTools.Window(link, body).ToJsonString()), "application/json");
            else if (path == "run")
                // Status only: the check never starts a run (that would start the CSI program); the Engine run step does.
                answer = (Encoding.UTF8.GetBytes(EngineRun.Status(false).ToJsonString()), "application/json");
            else if (path == "chat")
                answer = (Encoding.UTF8.GetBytes(chat ? """{"conv":"check","count":0,"items":[],"busy":false,"ready":true,"status":""}""" : """{"off":true}"""), "application/json");
            return Task.FromResult(answer);
        };
        var profile = Path.Combine(folder, "browser profile " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            HandModel.Reset();
            server.Start();
            var (dom, error) = ViewerCheck.Browse(browser, profile, seconds, "--dump-dom", "--virtual-time-budget=180000", "--window-size=1400,900", server.ViewUrl);
            if (dom is null) return new ViewerCheck.Result([], [], "the browser could not be run: " + error);
            if (ViewerCheck.Read(dom) is not { } items)
                return new ViewerCheck.Result([new ViewerCheck.Item("the Modeler page ran its check", false, "the page did not finish its check within the time the browser gave it")], [], null);
            return new ViewerCheck.Result(items, [], null);
        }
        finally
        {
            HandModel.Reset();
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); if (Directory.Exists(files)) Directory.Delete(files, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the test's work folder says where it is */ }
        }
    }

    /// <summary>
    /// The field test's steps for the Modeler's window, with the AI chat on and off: each menu item and each check is a
    /// step of its own, so a failure names the item. A PC with no usable browser records them as not tested.
    /// </summary>
    public static void FieldSteps(FieldContext ctx, string? browser = null)
    {
        foreach (var chat in new[] { true, false })
        {
            var label = chat ? "with the AI chat" : "without the AI";
            ViewerCheck.Result? seen = null;
            var ran = ctx.Step($"the Modeler's window opens on a scratch model, {label}", () =>
            {
                seen = Run(chat, Path.Combine(ctx.Folder, "Modeler window"), browser);
                if (seen.NotRun is not null) throw new FieldSkipException(seen.NotRun);
                return new { checked_items = seen.Items.Count };
            }) is not null;
            if (!ran || seen is null) continue;
            foreach (var item in seen.Items)
            {
                var name = $"Modeler window {label}, {item.Name}";
                if (item.Skipped) ctx.Skip(name, item.Detail.StartsWith("skipped: ", StringComparison.Ordinal) ? item.Detail["skipped: ".Length..] : item.Detail);
                else if (item.Ok) ctx.Step(name, () => item.Detail.Length > 0 ? item.Detail : "ok");
                else ctx.Fail(name, item.Detail);
            }
        }
    }
}
