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
      const add = (name, ok, detail, skipped) => items.push({name, ok: !!ok, detail: String(detail == null ? "" : detail).slice(0, 400), skipped: !!skipped});
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
        const out = document.createElement("script"); out.type = "application/json"; out.id = "cpm-selfcheck";
        out.textContent = JSON.stringify({items, webgl: !document.getElementById("hint").textContent.includes("lines only")}).replace(/</g, "\\u003c");
        document.body.appendChild(out); document.title = "cpm-selfcheck-done";
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
            return doc.RootElement.GetProperty("items").EnumerateArray().Select(i => new Item(i.GetProperty("name").GetString() ?? "", i.GetProperty("ok").GetBoolean(),
                i.GetProperty("detail").GetString() ?? "", i.GetProperty("skipped").GetBoolean())).ToList();
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
            var (dom, error) = Browse(browser, profile, seconds, "--dump-dom", "--virtual-time-budget=60000", address);
            if (dom is null) return new Result([], [], "the browser could not be run: " + error);
            if (Read(dom) is not { } items)
                return new Result([new Item("the Viewer page ran its check", false, "the page did not finish its check within the time the browser gave it" + (error is { Length: > 0 } ? $" ({Short(error)})" : ""))], [], null);
            var pictures = new List<string>();
            foreach (var (name, act) in Shots)
            {
                var picture = Path.Combine(folder, $"viewer {label} {name}.png");
                Browse(browser, profile, 60, "--screenshot=" + picture, "--window-size=1400,900", "--virtual-time-budget=8000", address + "#cpmshot=" + Uri.EscapeDataString(act));
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
            return new { checked_items = seen.Items.Count, pictures = seen.Pictures.Select(Path.GetFileName).ToArray() };
        }) is not null;
        if (!ran || seen is null) return;
        foreach (var item in seen.Items)
        {
            var name = "Viewer, " + item.Name;
            if (item.Skipped) ctx.Skip(name, item.Detail.StartsWith("skipped: ", StringComparison.Ordinal) ? item.Detail["skipped: ".Length..] : item.Detail);
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
