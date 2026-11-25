# CSI ProMax

An AI assistant for the CSI programs, built the way [Sketch ProMax](https://github.com/ahisham92/sketch-promax) is for Revit. It works with ETABS today. SAP2000 and SAFE share the same CSI API and plug into the same core later. Two parts need no CSI program on the PC: a Modeler that writes ETABS files, and a Viewer for reviewing a model and its results.

It was called ETABS ProMax until version 0.4.

**CSI ProMax never bypasses, emulates or circumvents CSI licensing.** The assistant attaches to an ETABS that the engineer has opened and licensed. The Modeler is our own program that writes ETABS text files. Analysis and design results come only from licensed ETABS.

## The parts

| Part | What it does | Needs |
| --- | --- | --- |
| **CSI ProMax in ETABS** | Its own chat window beside ETABS, opened from ETABS's Tools menu. Claude reads, models, loads, runs and checks the open model through ETABS's API. The full Sketch ProMax panel: workers and parallel jobs, home screen and prompt library, history, questions, autopilot and routines, phone connection, settings and updater, bug reports, files in and out (Excel, Word, PowerPoint, PDF, DWG, DXF). | Licensed ETABS 23, Claude Code on your Claude plan |
| **Results and checks** | Drift, torsion, soft and heavy stories, modes, design ratios, one check report, snapshots and compare. Every value is read from ETABS's own tables. | ETABS with a finished analysis |
| **Review pack and Viewer** | `export_pack` writes one `.cpmx` file (model, ETABS's result tables, checks) and a Viewer page with the pack inside. A reviewer opens the page in any browser: 3D view, deformed shape, design ratios in colour, tables, checks, and comments that go back to the engineer. | Nothing for the reviewer |
| **Modeler** | The same window with no ETABS behind it. The chat builds a model (stories, grids, sections, members, loads, combinations), a 3D view follows it in the browser, and it saves `.e2k` for ETABS to import. No analysis, no design. | Claude Code |

## Install

Nothing is an .exe and nothing needs administrator rights. It runs on the .NET 8 that ETABS 23 itself uses. See [docs/INSTALL.txt](docs/INSTALL.txt): copy the folder, add `CsiProMaxPlugin.dll` in ETABS under Tools > Add/Show Plugins, then Tools > CSI ProMax. Later versions download by themselves from this repository's releases.

The Modeler starts from the window's View menu, or from a desktop shortcut that the same menu makes.

## Safety

- You are always asked before a delete, a save or an analysis run, and before every change if you choose so. ETABS has no undo.
- The chat's tools and the 3D view are served on this PC only (127.0.0.1, a free port, a secret made at each start).
- Results are read from ETABS's tables. The assistant never computes them, and check limits are the engineer's or defaults that each answer names.
- Licence keys are CSI ProMax's own (`CPM1.`), tied to a PC code.

## Build and test

Needs the .NET 8 SDK. No ETABS. Builds on Linux, runs on Windows.

```
dotnet build CsiProMax.sln
dotnet run --project tests/CsiProMax.Tests
dotnet publish src/CsiProMax.Assistant -c Release -o "publish/CSI ProMax"
dotnet publish src/CsiProMax.Plugin    -c Release -o "publish/CSI ProMax"
```

```
dotnet CsiProMax.dll --selftest             checks each ETABS API call against the open model, read-only
dotnet CsiProMax.dll --selftest --changes   also adds and removes a few objects: use a scratch model
dotnet CsiProMax.dll --demo                 the window on a small stand-in model, without ETABS
dotnet CsiProMax.dll --modeler [file]       the Modeler, on a new model or an .e2k or .cpm file
```

Pushing a tag `vX.Y.Z` that matches the assistant's version publishes the release the updater downloads.

## Layout

```
src/CsiProMax.Core/       Model, .e2k reader and writer, .cpm file, checks, quantities. No CSI program, no Windows.
src/CsiProMax.Assistant/  The window: Etabs/ (the link to a CSI program), Tools/ (model, results, pack, Modeler tools), Panel/ (the Sketch ProMax panel).
src/CsiProMax.Viewer/     The Viewer page (one HTML file, no dependencies).
src/CsiProMax.Plugin/     The entry in ETABS's Tools menu: starts the assistant.
src/CsiProMax.Cli/        cpm: make, convert, check and measure models from the command line.
src/EtabsApiStub/         Build-time stand-in for the ETABSv1.dll types a plug-in names. Never shipped.
tests/CsiProMax.Tests/    Checks that run without ETABS.
```

## One core, one adapter per program

Everything above the link is shared: the panel, the licence, the workers, the chat engine, the updater, the checks, the pack and the Viewer. A program is:

1. A row in `Etabs/CsiProgram.cs` (process name, API file, ProgID). ETABS is checked. The SAP2000 and SAFE rows are there but not confirmed, so they are switched off.
2. The link, `ICsiLink`. `ComEtabs` drives a running program through the CSI API by name, so it serves any row. `MemoryEtabs` is the demo and `OfflineEtabs` is the Modeler.
3. A plug-in entry built against that program's API file, and that program's wording in the prompts.

## Status

Checked in ETABS Plus 23.2.0 on the owner's PC: the window and its menus, chat answers about the model, adding and deleting with approval, parallel jobs, files in each format, DWG reading. Checked here in tests and a browser only, not yet in ETABS: the result checks against a real analysed model, the review pack from a real model, and the Modeler window.
