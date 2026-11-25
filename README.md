# ETABS ProMax

An AI assistant for CSI ETABS, built the way [Sketch ProMax](https://github.com/ahisham92/sketch-promax) is for Revit, plus two things that work with no ETABS on the PC: a modeler that writes ETABS files, and a viewer for reviewing a model and its results.

See [docs/PLAN.md](docs/PLAN.md) for the architecture, the phases and what is decided.

## The three parts

1. **ETABS ProMax for ETABS** (needs licensed ETABS 23). Its own chat window beside ETABS, on your normal Claude plan through Claude Code, as the Sketch ProMax chat in Revit. Claude models, loads, runs and checks the open model through the ETABS API.
2. **ETABS ProMax Modeler** (no ETABS license). Our own model editor. It saves ETABS text files (.e2k) that licensed ETABS imports and analyzes. It does not run, emulate or unlock ETABS.
3. **ETABS ProMax Pack and Viewer.** One compressed .epmx file holds a model and all its results; a reviewer without ETABS opens it in a viewer laid out like ETABS and sends comments back.

## Status

Phase 1 (the shared core) and the first cut of phase 2 (the assistant beside ETABS) are in this repository. The results checks, the pack and viewer, and the modeler are not here yet.

| What | State |
| --- | --- |
| Model in ETABS's own shape (plan points, objects, per-story assignments) | Done |
| Read and write ETABS text files (.e2k, .$et), keeping what it doesn't interpret word for word | Reads a real ETABS 23.2 export and writes it back whole; files from the writer import into ETABS 23.2 (frame, slabs, wall, loads, combinations; checked 25 Nov 2026) |
| ETABS ProMax model file (.epm, JSON) | Done |
| Checks with no analysis: missing sections, free beam ends, unsupported columns, empty stories, loads on nothing | Done |
| Quantities: lengths, areas, concrete volumes by section | Done |
| `epm` command-line tool | Done |
| Assistant: its own chat window beside ETABS, running on the Claude Code installed on the PC | Works end to end on a stand-in model (chat, tools, approvals) |
| Assistant tools: read the model and ETABS's own tables (results included), select, add columns, beams, slabs and walls, sections, supports, loads, combinations, delete, save, run analysis | 17 tools. Checked in ETABS Plus 23.2.0 (25 Nov 2026): `--selftest --changes` passes all 28 steps, the plug-in opens the window from the Tools menu, and a chat request added a load pattern, 12 beam loads and a combination, confirmed in ETABS's own tables. Not yet checked there: delete, save and run analysis through the chat, and the approval cards |

New models start from the defaults ETABS 23.2 itself exports for a new model (materials, rebar sizes, analysis options, mass source, design preferences), because ETABS refuses a text file without them. Imports were checked in ETABS Plus 23.2.0 on small concrete models only; analysis and design of imported models have not been run yet.

## The assistant

A slim chat window beside ETABS, opened from ETABS's own Tools menu. It attaches to the ETABS that is already open and licensed on the PC, through ETABS's own API; it never starts, unlocks or stands in for ETABS. The chat runs on Claude Code on your own Claude plan, as in Sketch ProMax: the Claude desktop app is not needed, and the window offers to install Claude Code or sign in when they are missing.

- **No .exe and nothing to install.** It is a folder of DLLs that run on the .NET 8 that ETABS 23 itself needs. ETABS loads the plug-in (`EtabsProMaxPlugin.dll`), and the plug-in starts the assistant (`EtabsProMax.dll`) with that same .NET.
- With several ETABS windows open, the assistant works on the one it was started from.
- The window and the tools are served on this PC only (127.0.0.1, a free port, a secret made at each start).
- Claude gets the ETABS tools plus Read, Glob and Grep: no shell, no file editing, no web.
- You are always asked before a delete, a save or an analysis run, and before every change if you tick the box.
- Analysis and design results are read from ETABS's tables. The assistant never computes them itself.

```
dotnet publish src/EtabsProMax.Assistant -c Release -o "publish/ETABS ProMax"
dotnet publish src/EtabsProMax.Plugin    -c Release -o "publish/ETABS ProMax"
```

Copy the `ETABS ProMax` folder to the PC, then in ETABS: Tools > Add/Show Plugins, plug-in name `EtabsProMaxPlugin`, menu text `ETABS ProMax`, and the path of `EtabsProMaxPlugin.dll`. Without the plug-in, the same window starts with `dotnet EtabsProMax.dll`.

```
dotnet EtabsProMax.dll --selftest             checks each ETABS API call against the open model, read-only
dotnet EtabsProMax.dll --selftest --changes   also adds and removes a few objects: use a scratch model
dotnet EtabsProMax.dll --demo                 the window on a small stand-in model, without ETABS
```

Each pull request's checks also build the folder (the ETABS-ProMax download on the run's page).

## Try it

Needs the .NET 8 SDK. No ETABS.

```powershell
dotnet run --project src/EtabsProMax.Cli -- new-frame tower.e2k --x 6,6,6 --y 5,5 --stories 4
dotnet run --project src/EtabsProMax.Cli -- check tower.e2k
dotnet run --project src/EtabsProMax.Cli -- quantities tower.e2k
dotnet run --project src/EtabsProMax.Cli -- convert tower.e2k tower.epm
```

Then in ETABS: File > Import > ETABS .e2k Text File.

Tests: `dotnet run --project tests/EtabsProMax.Tests`.

## Layout

```
src/EtabsProMax.Core/     Model, .e2k reader and writer, .epm file, checks, quantities. No ETABS, no Windows.
src/EtabsProMax.Cli/      epm: make, convert, check and measure models from the command line.
src/EtabsProMax.Assistant/ The chat window beside ETABS: ETABS link, tools, MCP server, Claude Code engine, the page.
src/EtabsProMax.Plugin/   The entry in ETABS's Tools menu: starts the assistant.
src/EtabsApiStub/         Build-time stand-in for the three ETABSv1.dll types a plug-in names. Never shipped.
tests/EtabsProMax.Tests/  Checks that run without ETABS (dotnet run).
docs/PLAN.md              Architecture and phases.
```
