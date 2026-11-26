# CSI ProMax

An AI assistant for the CSI programs, built the way [Sketch ProMax](https://github.com/ahisham92/sketch-promax) is for Revit. It works with ETABS, SAP2000 and SAFE, one core with an adapter for each. Two parts need no CSI program on the PC: a Modeler that writes ETABS files, and a Viewer for reviewing a model and its results.

It was called ETABS ProMax until version 0.4.

**CSI ProMax never bypasses, emulates or circumvents CSI licensing.** The assistant attaches to an ETABS that the engineer has opened and licensed. The Modeler is our own program that writes ETABS text files. Analysis and design results come only from licensed ETABS.

## The parts

| Part | What it does | Needs |
| --- | --- | --- |
| **CSI ProMax in ETABS** | Its own chat window beside ETABS, opened from ETABS's Tools menu. Claude reads, models, loads, runs and checks the open model through ETABS's API. The full Sketch ProMax panel: workers and parallel jobs, home screen and prompt library, history, questions, autopilot and routines, phone connection, settings and updater, bug reports, files in and out (Excel, Word, PowerPoint, PDF, DWG, DXF). | Licensed ETABS 23, Claude Code on your Claude plan |
| **Results and checks** | Drift, torsion, soft and heavy stories, modes, design ratios, one check report, snapshots and compare. Every value is read from ETABS's own tables. | ETABS with a finished analysis |
| **Review pack and Viewer** | `export_pack` writes one `.cpmx` file (model, ETABS's result tables, checks) and a Viewer page with the pack inside, both in the model's own folder beside the model file. A reviewer opens the page in any browser: 3D view, deformed shape, every design result ETABS gives (steel, concrete reinforcing, composite, shear wall piers and spandrels, slab and punching) in colour and values as ETABS's Display Design Info shows them, tables, checks, and comments that go back to the engineer. Shear wall and other designs the API cannot read come from ETABS's design tables, or from the tables exported to Excel beside the model. | Nothing for the reviewer |
| **CSI ProMax in SAFE** | The same window from SAFE's Tools menu, working on slabs and foundations: slab, drop, mat and footing properties, soil under mats, point springs and supports, column loads, design strips and SAFE's editable tables. Checks read from SAFE's tables: punching shear, deflection, strip forces, soil pressure and uplift, slab design. The review pack shows punching ratios and soil pressure in the Viewer. | Licensed SAFE 20 or later |
| **Link with GeoProMax** | Soil and structure solved in turn for a raft, in SAFE and in ETABS. `export_soil_pressures` writes the soil pressures as a file GeoProMax opens (kN and m, whatever the model's units). GeoProMax works out the settlement and the spring each part of the raft needs, and saves a springs file (`.gpmsprings.json`). `apply_geopromax_springs` puts those springs under the raft: one soil property per zone (GPM-K1, GPM-K2 ...) on the raft's panels, in the model's units, or in SAFE point springs at the cell centres when the raft is one area. Run the analysis again and repeat until GeoProMax says the springs have converged. | Licensed SAFE 20 or later, or ETABS; GeoProMax |
| **CSI ProMax in SAP2000** | The same window from SAP2000's Tools menu. SAP2000 has no stories, so its tools build with joints, frames and areas in 3D, add materials and frame and area sections, support joints, load joints and members in any direction and release frame ends. Results come from SAP2000's analysis: joint displacements, frame forces, base reactions, modes and mass participation, a displacement check, and SAP2000's steel and concrete design ratios, in one check report for the pack. | Licensed SAP2000 (26 or later on .NET 8; older ones, see docs/INSTALL.txt) |
| **Modeler** | The same window with no ETABS behind it. The chat builds a model (stories, grids, sections, members, loads, combinations), a 3D view follows it in a CSI ProMax window of its own (WebView2, no browser tab), and it saves `.e2k` for ETABS to import, one floor or mat as `.f2k` for SAFE, or the whole model as `.s2k` for SAP2000 (stories become Z grid lines and groups, each rigid diaphragm a diaphragm constraint per story). No analysis, no design. | Claude Code |

## Install

Nothing is an .exe and nothing needs administrator rights. It runs on the .NET 8 that ETABS 23 itself uses. The quick way is one line in Windows PowerShell, which downloads the newest version into `Documents\CSI ProMax` and adds it to the Tools menu of ETABS, SAFE and SAP2000:

```powershell
irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1 | iex
```

CSI ProMax then keeps itself in those Tools menus: a hidden task at every Windows sign-in (`keep-plugins.ps1`) adds it back to any closed program that emptied its plug-in list. To uninstall: `irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/uninstall.ps1 | iex` (add `-Files` to delete the program files).

Without the AI chat (the Modeler, modelling by hand, the 3D view, review packs and the Viewer, and no Claude Code), use this line instead. The chat can be turned on or off later in the window's CSI ProMax menu, without closing the program:

```powershell
irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install-no-ai.ps1 | iex
```

With the chat on, the 3D view (View > Open the 3D view, and the Modeler's own view, each in a CSI ProMax window of its own) has a Chat tab: the same conversation as the window.

It works with ETABS 18 and later, SAFE 20 and later and SAP2000 20 and later. When it is done, the installer tries for each program what Tools > CSI ProMax does, and says yes or no. If a program does not open CSI ProMax, this line looks at why and prints a text to send back (it changes nothing):

```powershell
irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/check.ps1 | iex
```

By hand, see [docs/INSTALL.txt](docs/INSTALL.txt): copy the folder, add `CsiProMaxPlugin.dll` in ETABS under Tools > Add/Show Plugins, then Tools > CSI ProMax. SAP2000 takes `CsiProMaxSapPlugin.dll` from the same folder (plug-in name `CsiProMaxSapPlugin`). Later versions download by themselves from this repository's releases.

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
dotnet publish src/CsiProMax.Plugin.Sap -c Release -o "publish/CSI ProMax"
```

```
dotnet CsiProMax.dll --selftest             checks each ETABS API call against the open model, read-only
dotnet CsiProMax.dll --selftest --program sap2000   the same against the open SAP2000
dotnet CsiProMax.dll --selftest --changes   also adds and removes a few objects: use a scratch model
dotnet CsiProMax.dll --demo                 the window on a small stand-in model, without ETABS
dotnet CsiProMax.dll --modeler [file]       the Modeler, on a new model or an .e2k or .cpm file
```

Pushing a tag `vX.Y.Z` that matches the assistant's version publishes the release the updater downloads.

## Layout

```
src/CsiProMax.Core/       Model, .e2k reader and writer, .f2k writer (SAFE), .s2k writer (SAP2000), CSI table text, .cpm file, checks, quantities. No CSI program, no Windows.
src/CsiProMax.Assistant/  The window: Etabs/ (the link to a CSI program), Tools/ (model, results, pack, Modeler tools), Panel/ (the Sketch ProMax panel).
src/CsiProMax.Viewer/     The Viewer page (one HTML file, no dependencies).
src/CsiProMax.Plugin/     The entry in ETABS's Tools menu: starts the assistant (Launcher.cs is shared by every plug-in).
src/CsiProMax.Plugin.Safe/ The entry in SAFE's Tools menu, built against src/SafeApiStub.
src/CsiProMax.Plugin.Sap/ The entry in SAP2000's Tools menu, built against src/SapApiStub (unsigned: SAP2000 on .NET 8 binds
                          it by name; for an older SAP2000 put sap2000v1-public.snk beside the stub and it is signed with it).
src/CsiProMax.Cli/        cpm: make, convert, check and measure models from the command line.
src/EtabsApiStub/         Build-time stand-in for the ETABSv1.dll types a plug-in names. Never shipped.
tests/CsiProMax.Tests/    Checks that run without ETABS.
```

## One core, one adapter per program

Everything above the link is shared: the panel, the licence, the workers, the chat engine, the updater, the checks, the pack and the Viewer. A program is:

1. A row in `Etabs/CsiProgram.cs` (process name, API file, ProgID). ETABS is checked; SAP2000 and SAFE are switched on and not yet confirmed on a PC with them. The link uses the program's own API file, or CSI's common `CSiAPIv1.dll` when only that is there.
2. The link, `ICsiLink`. `ComEtabs` drives a running program through the CSI API by name, so it serves any row. `MemoryEtabs` is the demo and `OfflineEtabs` is the Modeler.
3. A plug-in entry built against that program's API file, its own tools (`Tools/ProgramTools.cs` picks them: SAFE's are in `Safe/`, SAP2000's in `Sap2000/`; a tool with a shared tool's name replaces it), and its wording (`Panel/Agent/ProgramPrompt.cs`: `Safe/SafePrompt.cs`, `Sap2000/SapPrompt.cs`). SAP2000 also has its own link on top of `ComEtabs` (`Sap2000/SapLink.cs`): no stories, and joints, frames and areas read one by one.

## Status

Checked in ETABS Plus 23.2.0 on the owner's PC: the window and its menus, chat answers about the model, adding and deleting with approval, parallel jobs, files in each format, DWG reading. Checked here in tests and a browser only, not yet in ETABS: the result checks against a real analysed model, the review pack from a real model, and the Modeler window.

SAFE: built and tested here against SAFE-shaped tables; not yet run in SAFE. The checks to run are in [docs/FIELD-TESTS.md](docs/FIELD-TESTS.md); the field test inside the program (type "run the field test" in the chat, or Help > Field test: everything) makes them itself, in a second SAFE window on a scratch model of its own, and saves a report and a zip to send back. To confirm on a PC with SAFE: the plug-in entry, the attach (SAFEv1.dll or CSiAPIv1.dll), `--selftest` in SAFE, the result table names the checks look for, and an `.f2k` from the Modeler imported into SAFE (compare with ETABS's own File > Export > Story as SAFE .F2K).

SAP2000: built and tested here against a stand-in SAP2000 (joints, frames, areas, analysis results) and the `.s2k` tables read back; not yet run in SAP2000. To confirm on a PC with SAP2000: the plug-in entry, the attach (SAP2000v1.dll), `--selftest --program sap2000`, the results and design calls on an analysed model, and an `.s2k` from the Modeler (or `cpm convert model.e2k model.s2k`) imported into SAP2000.
