# CSI ProMax: plan

Decided with Ahmed on 25 Nov 2026. The longer write-up, with the tool list per phase, is the project's plan document.

## What it is

Sketch ProMax rebuilt for ETABS: one shared core, three front doors.

```
                 Chat (Claude Code on the PC, your Claude plan)  |  Claude desktop app (optional)
                               |  MCP tools, the same set for all three
        +----------------------+-----------------------+--------------------------+
        |                      |                       |
  CSI ProMax for ETABS   CSI ProMax Modeler    CSI ProMax Viewer
  host + pane + plugin     our own model editor    opens .cpmx packs
  needs licensed ETABS     no ETABS license        reviewer needs no ETABS
        |                      |                       ^
        | CSI API, live        | saves .e2k            | .cpmx: model + results
        v                      v                       |
                 Licensed ETABS 23 (existing install) -+

  Shared core (this repository today): model, .e2k reader/writer, .cpm, checks, quantities
```

## Decisions

- **Feels standalone, like Sketch ProMax.** Its own chat window, driven by Claude Code on the PC with the normal Claude plan (no API key). The Claude desktop app is a second, optional way in. Recipes, learned tools, buttons, the Modeler and the Viewer work with no AI and no internet.
- **ETABS 23.**
- **No license bypass.** The Modeler is our own program that writes ETABS text files. Analysis and design results come only from licensed ETABS.
- **Stack.** C# .NET 8 for the core, the ETABS host and the tools; TypeScript, React and three.js for the Modeler and Viewer, in a WebView2 window and also in a plain browser so a reviewer installs nothing.
- **Design codes.** Whatever ETABS offers: tools read the list from ETABS, no fixed presets.
- **ETABS has no dockable panes and its plugins are modal,** so the pane is a slim window that sits beside ETABS, and the host is a separate process that attaches to running ETABS through the API. The plugin entry only shows the host.

## Phases

Each ends with something to try, and the next starts after it has been tried.

1. **Foundation** (in progress). Shared core, .e2k reader and writer. Gate: ETABS 23 imports our .e2k with no warnings, and three real models exported from ETABS read and write back unchanged.
2. **Assistant in ETABS.** Host, plugin, pane, chat; context, tables, modeling, loading and run tools; run-ETABS-code; recipes and learned tools. Gate: Claude builds and runs a small frame building in ETABS from a chat.
3. **Results and checks.** Results tools, drift, torsion and irregularity checks, design ratios, snapshots and compare, self-test. Gate: a check report on a live project that Ahmed agrees with.
4. **Pack and Viewer.** export_pack, the .cpmx format, the Viewer with model, deformed shapes, force diagrams, contours, tables and review comments. Gate: a colleague without ETABS reviews a real model with it.
5. **Standalone Modeler.** Editing UI on the Viewer's 3D engine, the same Claude tools on the offline model, save to .e2k. Gate: a model drawn with no ETABS open imports and runs cleanly in ETABS.

## The core's model

The model keeps ETABS's own shape: plan points; line objects (column, beam, brace) and area objects (floor, wall, ramp) that join points; and per-story assignments that say what each object is on each story. A column drawn once and assigned on ten stories is one object and ten assignments, as in ETABS.

Reading an ETABS file never loses anything. Keywords the core reasons about (section, restraint, diaphragm, thickness, ...) are named properties; every other keyword on a line is kept and written back; whole sections the core doesn't interpret (design preferences, mass source, functions, ...) are kept word for word and go back in the same place.

## Risks

1. **The .e2k format differs a little between ETABS versions.** Pin ETABS 23, keep real exported models as test files, and check every writer change against them.
2. **Plan limits.** Results tables are large; tools return counts and summaries, and page the rest.
3. **Professional responsibility.** The assistant proposes; the engineer approves. Checks are plain code, so they give the same answer every time.
