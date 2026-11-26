using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The Viewer's screen, checked: the Viewer page is opened on a review pack in a browser without a window (Edge or
/// Chrome, which Windows has), a script on the page presses each toolbar button, opens each result view, each table
/// and each side tab, and says for each whether it drew and whether the page reported an error; then pictures of the
/// main views are saved. One check for every program: the pack from ETABS, SAFE and SAP2000 goes through the same
/// page, so what is fixed for one is fixed for all. The page itself (viewer.html) is not changed: the script is added
/// to the copy the check opens.
/// </summary>
public static class ViewerCheck
{
    public sealed record Item(string Name, bool Ok, string Detail, bool Skipped = false);

    public sealed record Result(IReadOnlyList<Item> Items, IReadOnlyList<string> Pictures, string? NotRun);

    /// <summary>The pictures taken: a name, and the toolbar action that leads to it ("" for the page as it opens).</summary>
    public static readonly (string Name, string Act)[] Shots =
        [("model", ""), ("plan", "v:plan"), ("deformed", "dlg:deformed"), ("forces", "dlg:forces"), ("ratios", "r:ratios"), ("tables", "tables")];

    /// <summary>The Viewer page with the pack inside it and the checking script at its end.</summary>
    public static string Html(JsonObject content) =>
        Pack.ViewerHtml(content).Replace("</body>", "<script>" + Script + "</script></body>", StringComparison.Ordinal);

    /// <summary>
    /// Runs on the page. With "#cpmshot=act" in the address it only goes to that view (for a picture). Otherwise it
    /// tries everything and leaves its findings as JSON in an element with id cpm-selfcheck.
    /// </summary>
    public const string Script = """
    (function () {
      const errors = [];
      window.addEventListener("error", e => errors.push(String(e.message || e.error || e)));
      window.addEventListener("unhandledrejection", e => errors.push("promise: " + String(e.reason && e.reason.message || e.reason)));
      const wait = ms => new Promise(r => setTimeout(r, ms));
      const items = [];
      // The findings so far are on the page after every step, so a browser that stops early still hands back what ran
      // and where it stopped (0.4.53 field test: a big SAP2000 pack ran out of the browser's time and nothing came back).
      const flush = done => {
        if (/#cpmshot=/.test(location.hash)) return;
        let out = document.getElementById("cpm-selfcheck");
        if (!out) { out = document.createElement("script"); out.type = "application/json"; out.id = "cpm-selfcheck"; document.body.appendChild(out); }
        out.textContent = JSON.stringify({items, done, webgl: !document.getElementById("hint").textContent.includes("lines only")}).replace(/</g, "\\u003c");
      };
      const add = (name, ok, detail, skipped) => { items.push({name, ok: !!ok, detail: String(detail == null ? "" : detail).slice(0, 400), skipped: !!skipped}); try { flush(false); } catch (e) { } };
      // How much of a canvas is drawn on: the share of sampled pixels that differ from its corner.
      function ink(c) {
        try {
          if (!c || c.width < 40 || c.height < 40) return -1;
          const k = document.createElement("canvas"); k.width = c.width; k.height = c.height;
          const g = k.getContext("2d"); g.drawImage(c, 0, 0);
          const d = g.getImageData(0, 0, k.width, k.height).data;
          let n = 0, all = 0;
          for (let i = 0; i < d.length; i += 4 * 7) { all++; if (Math.abs(d[i] - d[0]) + Math.abs(d[i + 1] - d[1]) + Math.abs(d[i + 2] - d[2]) > 24) n++; }
          return n / all;
        } catch (e) { return -2; }
      }
      const drawn = () => Math.max(0, ...[...document.querySelectorAll("canvas")].filter(c => c.offsetParent !== null).map(ink));
      const closeDialogs = () => document.querySelectorAll(".dlg").forEach(d => { const x = d.querySelector(".dt .x"); if (x) x.click(); else d.remove(); });
      async function press(actName) {
        const b = document.querySelector(`#icons [data-act="${actName}"], #strip [data-act="${actName}"]`);
        if (!b) return "no such button";
        if (b.disabled) return "off";
        b.click(); await wait(250);
        // A window asking how to show the result: its first button applies it.
        const d = document.querySelector(".dlg");
        if (d) { const go = d.querySelector('[data-b="0"]'); if (go) go.click(); await wait(250); closeDialogs(); await wait(100); }
        return "";
      }
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
        try { closeDialogs(); } catch (e) { }
      }
      async function run() {
        await wait(900);
        const shot = /#cpmshot=(.*)$/.exec(location.hash);
        if (shot) { const a = decodeURIComponent(shot[1]); if (a) await press(a); await wait(600); document.title = "cpm-shot-done"; return; }

        await step("the page opens on the pack", async () => {
          const meta = (document.getElementById("meta") || {}).textContent || "";
          if (errors.length) return {fail: "errors while opening: " + errors.join("; ")};
          if (!meta.trim()) return {fail: "the title bar names no model, so the pack was not loaded"};
          return {ok: meta};
        });
        await step("the model is drawn", async () => {
          const share = drawn();
          return share > 0.002 ? {ok: `${(share * 100).toFixed(1)}% of the view is drawn on` + (document.getElementById("hint").textContent.includes("lines only") ? " (lines only: this browser has no 3-D drawing)" : "")}
            : {fail: `the view is empty (${share})`};
        });
        await step("the Model Explorer lists the model", async () => {
          const leaves = document.querySelectorAll("#tree .leaf, #tree [data-table]").length;
          return leaves > 0 ? {ok: leaves + " entries"} : {fail: "the tree is empty"};
        });
        // Every button of the two toolbars, as an engineer would press them (not "open", which asks for a file).
        const acts = [...document.querySelectorAll("#icons [data-act], #strip [data-act]")].map(b => [b.dataset.act, b.title, !!b.dataset.res]);
        const needs = {storyUp: "v:plan", storyDown: "v:plan", linePrev: "v:elev", lineNext: "v:elev", anim: "dlg:deformed"};
        for (const [a, title, isResult] of acts) {
          if (a === "open" || a === "saveComments" || a === "copyNames") continue;
          await step(`button: ${title}`, async () => {
            // A button that only works in one kind of view is pressed in that view: the story buttons in a plan,
            // the grid line buttons in an elevation, the animation on the deformed shape.
            const first = needs[a];
            if (first) { const lead = await press(first); if (lead === "off") return {skipped: `skipped: it needs "${first}" first, and that button is greyed out for this model`}; await wait(150); }
            const said = await press(a);
            if (said === "off") return {skipped: "skipped: the button is greyed out, this model has nothing for it (no such results, stories or grid lines)"};
            if (said) return {fail: said};
            await wait(150);
            const share = drawn();
            if (a === "tables") return document.getElementById("dock").hidden ? {ok: "tables closed"} : {ok: "tables open"};
            return share > 0.002 ? {ok: `drawn (${(share * 100).toFixed(1)}%)`} : {fail: `nothing is drawn after it (${share})`};
          });
          // Back to the plain model in a 3-D view before the next button.
          try { if (a === "anim") { const on = document.querySelector('[data-act="anim"].on'); if (on) on.click(); } if (needs[a]) { const back = document.querySelector(needs[a].startsWith("v:") ? '[data-act="v:3d"]' : '[data-act="r:model"]'); if (back && !back.disabled) back.click(); } if (isResult) { const m = document.querySelector('[data-act="r:model"]'); if (m && !m.disabled) m.click(); } if (a === "plot") { const p = document.querySelector('[data-act="plot"].on'); if (p) p.click(); } await wait(60); } catch (e) { }
        }
        // Every item of the Display and Design menus, as an engineer would choose them: each window it opens is
        // applied with its first button (OK), and the view must draw with no error. A menu item added later is
        // pressed too, so every result display is covered by rule.
        const leaves = [];
        try { const walk = l => (l || []).forEach(i => { if (!i) return; if (Array.isArray(i[1])) walk(i[1]); else leaves.push([i[0], i[1], !!i[3]]); }); const m = menus(); walk(m.Display); walk(m.Design); } catch (e) { add("menus", false, "the menus could not be read: " + (e && e.message || e)); }
        for (const [label, a, off] of leaves) {
          if (!a || a === "tables" || a === "anim" || a === "print") continue;
          await step(`menu: ${label}`, async () => {
            if (off) return {skipped: "skipped: greyed out, this pack has nothing for it"};
            act(a); await wait(250);
            const d = document.querySelector(".dlg");
            if (d) { const go = d.querySelector('[data-b="0"]'); if (go) go.click(); await wait(300); }
            const share = drawn();
            // A display the Viewer can judge (loads, shell contours) must show some of its results, not only the model.
            const shows = typeof resultShown === "function" ? resultShown() : null;
            if (shows === "") return {fail: "the model is drawn, but none of this display's results are on it"};
            return share > 0.002 ? {ok: `drawn (${(share * 100).toFixed(1)}%)` + (shows ? ": " + shows : "")} : {fail: `nothing is drawn after it (${share})`};
          });
          try { closeDialogs(); const m = document.querySelector('[data-act="r:model"]'); if (m && !m.disabled) m.click(); const p = document.querySelector('[data-act="plot"].on'); if (p) p.click(); await wait(60); } catch (e) { }
        }
        // Every table the pack holds, opened in the tables window.
        const tableNames = [...document.querySelectorAll("#tree [data-table]")].map(e => e.dataset.table);
        if (!tableNames.length) add("tables", true, "skipped: the pack has no result tables", true);
        for (const t of tableNames) {
          await step(`table: ${t}`, async () => {
            document.querySelector(`#tree [data-table="${CSS.escape(t)}"]`).click(); await wait(200);
            const grid = document.getElementById("grid");
            const shown = (document.getElementById("tname") || {}).textContent || "";
            if (document.getElementById("dock").hidden) return {fail: "the tables window did not open"};
            if (!grid || !grid.textContent.trim()) return {fail: "the table is shown empty"};
            return {ok: `${shown}: ${grid.querySelectorAll("tr").length || grid.children.length} rows shown`};
          });
        }
        // Selection as the programs do it (Ahmed 2026-11-26): a left-to-right window round a support selects that joint,
        // a right click opens the object's information with its loads, and the Filter keeps only the checked kinds.
        const view3d = async () => {
          // A 3-D view, waited for until its camera has stopped moving and it is drawn, so its projection is the one shown.
          const b = document.querySelector('[data-act="v:3d"]'); if (b && !b.disabled) b.click(); await wait(150);
          const p = panes[active]; if (p.mode !== "3d") setMode(p, "3d");
          for (let i = 0; i < 40 && (p.anim || waiting); i++) await wait(100);
          // No camera movement: the view is set at once, and its projection worked out here as the next drawing will.
          fit(p, false); p.anim = null; draw(); await wait(200);
          if (p.R) p.M = matrices(p, p.R.w, p.R.h);
          return p;
        };
        const objInfoProblem = d => {
          const tabs = [...d.querySelectorAll(".itabs button")].map(b => b.textContent), buttons = [...d.querySelectorAll(".df button")].map(b => b.textContent).join(", ");
          const first = (program === "sap2000" ? "Location" : "Geometry") + ", Assignments, Loads";
          if (tabs.slice(0, 3).join(", ") !== first) return `its tabs are ${tabs.join(", ")}, not ${first} as in ${program.toUpperCase()}`;
          if (window.cpmLive ? tabs.length !== 3 : tabs.slice(3).some(t => !["Results", "Design", "Comments"].includes(t))) return "it has tabs the program does not: " + tabs.join(", ");
          if (buttons !== "OK, Cancel") return "its buttons are " + buttons + ", not OK, Cancel";
          if (!d.querySelector("fieldset.oid") || !/Label/.test(d.querySelector("fieldset.oid").textContent)) return "it has no Object ID box";
          if (d.querySelectorAll("table.pg").length < 3) return "its tabs are not property grids";
          const row = d.querySelector("table.pg tr[data-n]"); row.click();
          if (!d.querySelector(".pgdesc b") || d.querySelector(".pgdesc b").textContent !== row.dataset.n) return "a click on a property does not show its description underneath";
          return "";
        };
        await step("the Model Explorer is the program's: Model, Display, Tables and Reports tabs, the Model tree in the program's order", async () => {
          const tabs = [...document.querySelectorAll("#tree .xtabs button")].map(b => b.textContent).join(", ");
          if (tabs !== "Model, Display, Tables, Reports") return {fail: "the explorer's tabs are " + (tabs || "missing")};
          const top = [...document.querySelectorAll('#tree .xpage[data-x="model"] > details > details > summary')].map(s => s.firstChild.textContent.trim());
          const want = ["Project", "Structure Layout", "Properties", "Structural Objects", "Groups", "Loads", "Named Output Items", "Named Plots"];
          if (top.join("|") !== want.join("|")) return {fail: "the Model tree reads " + top.join(", ")};
          const menus = [...document.querySelectorAll("#menubar .menu:not([hidden])")].map(m => m.dataset.menu);
          for (const m of ["File", "Edit", "View", "Define", "Select", "Analyze", "Display", "Design", "Options", "Tools", "Help"]) if (!menus.includes(m)) return {fail: "the menu bar has no " + m + " menu: " + menus.join(", ")};
          if (!document.getElementById("vname").textContent) return {fail: "the status bar does not name the view"};
          return {ok: tabs + "; " + top.join(", ") + "; menus " + menus.join(" ")};
        });
        await step("selection: a left-to-right window round a support selects the support joint", async () => {
          if (!supports.length) return {skipped: "skipped: the model has no supports"};
          const p = await view3d(), s = supports.find(x => supportShown(p, x) && p.M.to(x.p)[3]);
          if (!s) return {fail: `no support is in the 3-D view (${supports.length} supports, view ${p.mode}, first at ${JSON.stringify(supports[0].p)} drawn at ${JSON.stringify(p.M.to(supports[0].p).map(v => typeof v === "number" ? Math.round(v) : v))}, camera ${JSON.stringify(p.cam)}, window ${JSON.stringify(p.R)})`};
          const q = p.M.to(s.p); boxSelect(p, {x0: q[0] - 10, x1: q[0] + 10, y0: q[1] - 10, y1: q[1] + 10}, false, false);
          if (!selJ.has(s.name)) return {fail: `the window round support ${s.name} left it unselected`};
          const said = document.getElementById("sel").textContent;
          if (!/Joint/.test(said)) return {fail: "the joint is selected but the status bar does not count it: " + said};
          rightClick(p, q[0], q[1], false); await wait(120);
          const d = [...document.querySelectorAll(".dlg")].pop();
          if (!d || !/Joint Information/.test(d.textContent)) return {fail: "a right click on the support opened no Joint Information"};
          // The programs' Object Information (Ahmed 2026-11-26): Object ID, the programs' own tabs as property grids, OK and Cancel.
          const bad = objInfoProblem(d); if (bad) return {fail: "Joint Information: " + bad};
          if (!d.querySelector('tr[data-n="Restraints"]')) return {fail: "Joint Information has no Restraints row in Assignments"};
          return {ok: `support ${s.name} selected (${said}); its right click opens Joint Information in the program's own layout`};
        });
        await step("selection: right click on an object opens its information, with a Loads tab holding its loads", async () => {
          const p = await view3d(), vis = visOf(p), shown = frames.concat(areas).filter(o => vis[o.id]);
          if (!shown.length) return {fail: `nothing is drawn in the 3-D view (${p.mode}, ${p.vis ? p.vis.filter(Boolean).length : "no"} shown, ${frames.length + areas.length} objects, ${hidden.objs.size} hidden)`};
          sel.clear(); selJ.clear(); selChanged();
          const span = o => o.type === "f" ? Math.hypot(...[0, 1].map(i => p.M.to(o.a)[i] - p.M.to(o.b)[i])) : 0;
          const o = shown.filter(o => o.type === "f").sort((a, b) => span(b) - span(a))[0] || shown[0], mid = [0, 1, 2].map(i => o.type === "f" ? (o.a[i] + o.b[i]) / 2 : o.pts.reduce((t, c) => t + c[i], 0) / o.pts.length), q = p.M.to(mid);
          rightClick(p, q[0], q[1], false); await wait(150);
          let d = [...document.querySelectorAll(".dlg")].pop();
          if (!d || !/Information/.test(d.textContent)) return {fail: `a right click on ${o.name} opened no information window (${JSON.stringify(pickAt(p, q[0], q[1]))} at ${q.slice(0, 2).map(Math.round)}; ${document.querySelectorAll(".dlg").length} windows)`};
          const bad = objInfoProblem(d); if (bad) return {fail: `${o.name}'s information window: ${bad}`};
          closeDialogs();
          const loaded = shown.find(x => loadRowsOf(x).length);
          if (!loaded) return {ok: "information window with a Loads tab opened; no object in this pack has loads assigned"};
          infoWindow(loaded.id); await wait(120); d = [...document.querySelectorAll(".dlg")].pop();
          const n = loadRowsOf(loaded).reduce((k, x) => k + x.rows.length, 0), page = d.querySelectorAll(".ipage")[2], heads = [...page.querySelectorAll("tr.pgh")].map(h => h.dataset.r);
          const shownLoads = [...page.querySelectorAll("tr[data-p]")].filter(tr => heads.includes(tr.dataset.p)).length, tab = `Loads (${shownLoads} under ${heads.length} load patterns)`;
          if (shownLoads !== n) return {fail: `${loaded.name} has ${n} load rows but its Loads tab lists ${shownLoads}`};
          // A second right click replaces the window, never a second one beside it (Ahmed 2026-11-26).
          const other = shown.find(x => x !== loaded) || loaded, oq = p.M.to([0, 1, 2].map(i => other.type === "f" ? (other.a[i] + other.b[i]) / 2 : other.pts.reduce((t, c) => t + c[i], 0) / other.pts.length));
          rightClick(p, oq[0], oq[1], false); await wait(120); if (supports.length) { jointInfo(supports[0].name); await wait(80); }
          const open = document.querySelectorAll(".dlg.info").length;
          if (open !== 1) return {fail: `after more right clicks ${open} information windows are open, not one`};
          closeDialogs();
          sel.clear(); selJ.clear(); sel.add(loaded.id); selChanged();
          const props = document.getElementById("props").textContent;
          return /Loads/.test(props) ? {ok: `${kindWord(loaded)} ${loaded.name}: ${tab}, and Properties lists them`} : {fail: `Properties of ${loaded.name} do not list its ${n} loads`};
        });
        await step("selection: the Filter keeps only the checked kinds of the selection", async () => {
          const p = await view3d(); act("selAll"); act("selSupports"); await wait(80);
          const cats = selCats(); if (cats.length < 2) return {skipped: "skipped: the selection has only one kind of object"};
          const before = sel.size + selJ.size; selFilter(); await wait(120);
          const d = [...document.querySelectorAll(".dlg")].pop(), g = d && d.querySelector("[data-g]");
          if (!g) return {fail: "the Filter window did not open"};
          g.checked = false; g.dispatchEvent(new Event("change")); d.querySelector('[data-b="0"]').click(); await wait(80);
          const after = sel.size + selJ.size, left = selCats().map(c => c.label);
          if (!(after < before)) return {fail: `unchecking ${g.dataset.g} left ${after} of ${before} selected`};
          if (selCats().some(c => (c.k.startsWith("j:") ? "Joints" : c.label.split(" · ")[0]) === g.dataset.g)) return {fail: `${g.dataset.g} are still selected after unchecking them`};
          act("selNone");
          return {ok: `${before} selected, ${g.dataset.g} unchecked, ${after} kept (${left.slice(0, 4).join(", ")})`};
        });
        await step("selection: the Filter button sits at the bottom right with the count selected, as in Revit, and opens the Filter", async () => {
          await view3d(); act("selNone"); await wait(60);
          const b = document.getElementById("fbFilter"), foot = document.querySelector("footer");
          if (!b || !foot || foot.lastElementChild !== b) return {fail: "there is no Filter button at the right end of the status bar"};
          const r = b.getBoundingClientRect();
          if (!r.width || r.right < innerWidth - 4) return {fail: `the Filter button is not at the bottom right (right edge ${Math.round(r.right)} of ${innerWidth})`};
          if (!b.disabled) return {fail: "the Filter button is on with nothing selected"};
          act("selAll"); await wait(80);
          const n = sel.size + selJ.size, shown = +document.getElementById("fbN").textContent;
          if (b.disabled || shown !== n) return {fail: `with ${n} selected the button is ${b.disabled ? "off" : "on"} and shows ${shown}`};
          const before = document.querySelectorAll(".dlg").length; b.click(); await wait(120);
          const d = [...document.querySelectorAll(".dlg")].pop();
          const ok = document.querySelectorAll(".dlg").length > before && d && d.querySelector("#fAll") && d.querySelector("#fNone") && d.querySelector("[data-c]");
          closeDialogs(); act("selNone");
          return ok ? {ok: `${n} selected shown on the button; it opens the Filter with Check All / Check None`} : {fail: "clicking it did not open the Filter with its kinds, Check All and Check None"};
        });
        for (const tab of ["props", "checks", "comments"]) {
          await step(`side tab: ${tab}`, async () => {
            const b = document.querySelector(`[data-tab="${tab}"]`);
            if (!b || b.hidden) return {skipped: "skipped: this tab is not shown for this pack"};
            b.click(); await wait(80);
            const page = document.getElementById(tab);
            return page && page.classList.contains("on") && page.textContent.trim() ? {ok: page.textContent.trim().slice(0, 390)} : {fail: "the tab is empty or did not open"};
          });
        }
        if (errors.length) add("no errors on the page during the whole check", items.every(i => i.ok), errors.slice(0, 5).join("; "));
        else add("no errors on the page during the whole check", true, "");
      }
      run().catch(e => add("the check itself", false, String(e && e.message || e))).finally(() => {
        if (/#cpmshot=/.test(location.hash)) return;
        flush(true); document.title = "cpm-selfcheck-done";
      });
    })();
    """;

    /// <summary>Reads what the script left on the page (the page's text as the browser printed it).</summary>
    public static IReadOnlyList<Item>? Read(string dom)
    {
        const string open = "<script type=\"application/json\" id=\"cpm-selfcheck\">";
        var at = dom.IndexOf(open, StringComparison.Ordinal);
        if (at < 0) return null;
        var end = dom.IndexOf("</script>", at, StringComparison.Ordinal);
        if (end < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(dom[(at + open.Length)..end]);
            var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(i => new Item(i.GetProperty("name").GetString() ?? "", i.GetProperty("ok").GetBoolean(),
                i.GetProperty("detail").GetString() ?? "", i.GetProperty("skipped").GetBoolean())).ToList();
            // A check the browser stopped part way: what ran stands, and the stop is a finding of its own.
            if (doc.RootElement.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.False)
                items.Add(new Item("the Viewer page ran its check to the end", false, $"the browser stopped the page after {items.Count} items" + (items.Count > 0 ? $", the last being \"{items[^1].Name}\"" : "") + "; the rest were not checked"));
            return items;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>A browser that can run without a window: Edge or Chrome where Windows keeps them (CPM_BROWSER names another).</summary>
    public static string? FindBrowser()
    {
        var named = Environment.GetEnvironmentVariable("CPM_BROWSER");
        if (!string.IsNullOrWhiteSpace(named) && File.Exists(named)) return named;
        var places = new List<string>();
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("LocalAppData") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            places.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            places.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        }
        places.AddRange(["/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/opt/pw-browsers/chromium"]);
        return places.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Checks the Viewer on <paramref name="content"/> and saves pictures into <paramref name="folder"/>. When no
    /// browser can be run (none found, or this PC does not allow it) the result says so and nothing is checked.
    /// </summary>
    public static Result Run(JsonObject content, string folder, string label, string? browser = null, int seconds = 120)
    {
        browser ??= FindBrowser();
        if (browser is null) return new Result([], [], "no Edge or Chrome was found on this PC to open the Viewer in");
        Directory.CreateDirectory(folder);
        var page = Path.Combine(folder, $"viewer check {label}.html");
        File.WriteAllText(page, Html(content), new UTF8Encoding(false));
        var profile = Path.Combine(folder, "browser profile " + Guid.NewGuid().ToString("N")[..6]);
        var address = new Uri(page).AbsoluteUri;
        try
        {
            var (virtualMs, realSeconds, shotSeconds) = Allowance(content, seconds);
            var (dom, error) = Browse(browser, profile, realSeconds, "--dump-dom", "--virtual-time-budget=" + virtualMs.ToString(System.Globalization.CultureInfo.InvariantCulture), address);
            if (dom is null) return new Result([], [], "the browser could not be run: " + error);
            if (Read(dom) is not { } items)
                return new Result([new Item("the Viewer page ran its check", false, $"the page did not finish its check within the time the browser gave it ({realSeconds} s, {virtualMs / 1000} s of page time, a page of {new FileInfo(page).Length / 1048576.0:0.0} MB)" + (error is { Length: > 0 } ? $" ({Short(error)})" : ""))], [], null);
            var pictures = new List<string>();
            foreach (var (name, act) in Shots)
            {
                var picture = Path.Combine(folder, $"viewer {label} {name}.png");
                Browse(browser, profile, shotSeconds, "--screenshot=" + picture, "--window-size=1400,900", "--virtual-time-budget=8000", address + "#cpmshot=" + Uri.EscapeDataString(act));
                if (File.Exists(picture) && new FileInfo(picture).Length > 2000) pictures.Add(picture);
            }
            return new Result(items, pictures, null);
        }
        finally
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the test's work folder says where it is */ }
        }
    }

    /// <summary>
    /// How long the browser may take on a pack: the page's own waits grow with the tables it opens one by one (about
    /// half a second each) and the menu items, and loading grows with the page's size. 0.4.53: SAP2000's pack with
    /// every pattern's load rows outgrew the old fixed 60 s of page time and 120 s, and no finding came back.
    /// </summary>
    public static (int VirtualMs, int RealSeconds, int ShotSeconds) Allowance(JsonObject content, int seconds = 120)
    {
        var tables = (content["tables"] as JsonObject)?.Count ?? 0;
        var megabytes = content.ToJsonString().Length / 1048576.0;
        var virtualMs = 90_000 + tables * 600;
        var real = Math.Max(seconds, 60 + virtualMs / 1000 + (int)Math.Ceiling(megabytes * 6));
        return (virtualMs, real, 60 + (int)Math.Ceiling(megabytes * 4));
    }

    private sealed class Disposer(Action done) : IDisposable { public void Dispose() => done(); }

    private static string Short(string text) => text.Length > 300 ? text[..300] + "..." : text;

    internal static (string? Out, string? Error) Browse(string browser, string profile, int seconds, params string[] arguments)
    {
        var info = new ProcessStartInfo(browser) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--hide-scrollbars", "--enable-unsafe-swiftshader", "--user-data-dir=" + profile })
            info.ArgumentList.Add(a);
        if (!OperatingSystem.IsWindows()) info.ArgumentList.Add("--no-sandbox");
        foreach (var a in arguments) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return (null, "it did not start");
            var clock = Stopwatch.StartNew();
            // On a build machine each browser run says how long it took, so a slow or stuck one is seen in the log.
            using var said = Environment.GetEnvironmentVariable("CI") == "true" ? new Disposer(() => Console.Error.WriteLine($"   [browser {clock.Elapsed.TotalSeconds:0.0} s of {seconds} s] {string.Join(" ", arguments.Select(x => x.Length > 90 ? "..." + x[^60..] : x))}")) : null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(seconds * 1000))
            {
                // The check's own browser, started a moment ago by this call.
                try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return (null, $"it did not finish within {seconds} seconds");
            }
            return (output.Result, error.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return (null, ex.Message); }
    }

    /// <summary>
    /// A Viewer display the program keeps no table for, so on its pack the item is rightly greyed out (the Viewer shows
    /// what the program shows, nothing extra: Ahmed 2026-11-26). Judged by the program's own table list, with the words
    /// the Viewer itself looks for (viewer.html, the Display menu): SAFE 23.1 lists modal, section cut and imported
    /// response spectrum tables, so those are tested; it lists no pier or spandrel forces, hinge or pushover tables
    /// (0.4.56 tester run, 286 tables). Null when the program has such a table, or for any other item: tested as usual.
    /// </summary>
    public static string? NotInProgram(IReadOnlyCollection<string> programTables, string program, string item)
    {
        if (programTables.Count == 0) return null;
        foreach (var (words, look, what) in new[]
        {
            ("Pier and Spandrel", "pierforces|spandrelforces", "pier or spandrel forces"), ("Hinge", "hinge", "hinge results"),
            ("Pushover", "pushover|capacitycurve", "pushover curves"), ("Response Spectrum", "responsespectrum", "response spectrum cases"),
            ("Mode Shape", "modal", "modal results"), ("Modal Results", "modal", "modal results"), ("Section Cut", "sectioncut", "section cut forces"),
        })
            if (item.Contains(words, StringComparison.OrdinalIgnoreCase))
                return programTables.Any(t => System.Text.RegularExpressions.Regex.IsMatch(ResultTools.Norm(t), look)) ? null
                    : $"{program} has no {what} (none of its {programTables.Count} tables holds them)";
        return null;
    }

    /// <summary>
    /// The field test's Viewer steps, the same for every program: a pack of the model <paramref name="link"/> has open
    /// is made, the Viewer is opened on it, and every item the page checked is recorded as its own step, with the
    /// pictures saved in the test's folder (they go into the zip). A PC with no usable browser records "not tested".
    /// </summary>
    public static void FieldSteps(FieldContext ctx, ICsiLink link, string program, string? browser = null)
    {
        Result? seen = null;
        JsonObject? content = null;
        var ran = ctx.Step($"the Viewer opens on the scratch {program} model's pack", () =>
        {
            var cases = link.LoadCases();
            JsonObject? checks = null;
            try { checks = ProgramTools.Report(JsonDocument.Parse("""{"drift_limit":0.002}""").RootElement, link).AsObject(); }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { /* the pack goes without checks; the checks have their own steps */ }
            var pack = Pack.Build(link, [], cases, link.Combos(), checks, null, mostRows: 20000, by: "CSI ProMax field test");
            content = pack.Content;
            seen = Run(pack.Content, Path.Combine(ctx.Folder, "Viewer"), program, browser);
            if (seen.NotRun is not null) throw new FieldSkipException(seen.NotRun);
            var (virtualMs, realSeconds, _) = Allowance(pack.Content);
            return new { checked_items = seen.Items.Count, pictures = seen.Pictures.Select(Path.GetFileName).ToArray(), pack_tables = (pack.Content["tables"] as JsonObject)?.Count ?? 0, pack_mb = Math.Round(pack.Content.ToJsonString().Length / 1048576.0, 1), page_seconds = virtualMs / 1000, browser_seconds = realSeconds };
        }) is not null;
        if (!ran || seen is null) return;
        // Display > Load Assigns draws what the pack holds: every load table the model has rows in must be there.
        ctx.Step($"the {program} model's pack holds its load assignments, for Display > Load Assigns", () =>
        {
            var have = link.Tables().Where(t => !t.Empty && PackTables.IsLoadTable(t.Name)).ToList();
            if (have.Count == 0) throw new FieldSkipException($"the scratch {program} model has no load assignment tables with rows (its tables: {string.Join(", ", link.Tables().Where(t => !t.Empty && ResultTools.Norm(t.Name).Contains("load", StringComparison.Ordinal)).Select(t => t.Name).Take(20))})");
            var packed = content!["tables"]!.AsObject();
            var missing = have.Where(t => !packed.ContainsKey(t.Name)).Select(t => $"{t.Name} (taken as {PackTables.Kind(t) ?? "nothing"}{((content["manifest"]?["left_out_tables"] as JsonArray)?.Any(x => (string?)x == t.Name) == true ? ", left out for size" : "")})").ToList();
            FieldContext.Expect(missing.Count == 0, "the pack has no " + string.Join("; ", missing));
            return string.Join(", ", have.Select(t => t.Name));
        });
        var programTables = Array.Empty<string>();
        try { programTables = link.Tables().Select(t => t.Name).ToArray(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { /* then no greyed-out item is taken as right */ }
        foreach (var item in seen.Items)
        {
            var name = "Viewer, " + item.Name;
            if (item.Skipped && NotInProgram(programTables, program, item.Name) is { } none) ctx.Step(name, () => $"greyed out, as it should be: {none}");
            else if (item.Skipped) ctx.Skip(name, item.Detail.StartsWith("skipped: ", StringComparison.Ordinal) ? item.Detail["skipped: ".Length..] : item.Detail);
            else if (item.Ok) ctx.Step(name, () => item.Detail.Length > 0 ? item.Detail : "ok");
            else ctx.Fail(name, item.Detail);
        }
        ctx.Step($"Viewer, the page shows the same numbers of columns, beams, braces, floors and walls as the {program} model's pack", () =>
        {
            var shown = seen.Items.FirstOrDefault(i => i.Name == "side tab: props")?.Detail ?? "";
            var read = System.Text.RegularExpressions.Regex.Match(shown, @"Columns(\d+)Beams(\d+)Braces(\d+)Floors(\d+)Walls(\d+)");
            FieldContext.Expect(read.Success, "the Properties tab does not list the numbers of objects: " + shown);
            int Of(string list, string kind) => (content!["model"]?[list] as JsonArray)?.Count(o => (string?)(o is JsonArray row ? row[1] : o?["k"]) == kind) ?? 0;
            var has = new[] { Of("frames", "column"), Of("frames", "beam"), Of("frames", "brace"), Of("areas", "floor"), Of("areas", "wall") };
            var page = Enumerable.Range(1, 5).Select(i => int.Parse(read.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            FieldContext.Expect(has.SequenceEqual(page), $"the page shows {string.Join(", ", page)} columns, beams, braces, floors, walls and the pack holds {string.Join(", ", has)}");
            return string.Join(", ", page);
        });
        ctx.Step($"the Viewer's pictures of the {program} model are saved", () =>
        {
            FieldContext.Expect(seen.Pictures.Count == Shots.Length, $"{seen.Pictures.Count} of {Shots.Length} pictures were saved");
            return string.Join(", ", seen.Pictures.Select(Path.GetFileName));
        });
    }
}
