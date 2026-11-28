# CSI ProMax hand-off

Updated with every release (Ahmed, 2026-11-27): what each release did, newest first, and the tasks still open.
Times are Cairo. Every PC takes a release through the live updater (hourly check since 0.4.69); nothing to paste.

## Still to do

0. **On hold (Ahmed, 2026-11-28 01:09):** form capturing and all other jobs stop after 0.4.75 until Ahmed says to continue.
1. **ETABS first (Ahmed's order).** Every remaining ETABS 23.2 form and menu identical field by field, each with a field round trip that reads the values back in ETABS. Pending forms are listed by the field test in `field/forms-check.md` on the test PC.
2. **Field test to 0 FAIL.** Rerun only the failed checks on each new release until 0, then one full run. Fixes for the 0.4.73 failures are in 0.4.75 (PR 330, 331, 336, 342); field runs are on hold until Ahmed restarts them.
3. **Lossless real-model round trip.** ETABS .e2k / SAP2000 .s2k / SAFE .f2k in and out with nothing lost on the real example models; `model table differences.txt` and `result differences.txt` must come back empty.
4. **Results pack timing.** Read, convert and load every result in 3 minutes for small models and 6 minutes for large ones, with no table left out and the PC never locked.
5. **SAP2000 and SAFE (paused, ETABS first).** SAP2000: remaining Define/Analyze/Display forms. SAFE: forms and the tester's SAFE 22.7/23.1 reruns (the reference data gathering stays in every release until the data is received).
6. **After the forms and 0 FAIL.** Close the program after a run (on hold, `KeepProgramOpenAfterRun` is true for now); central model with local copies for several users; side-by-side form pictures for every program.

Standing rules: never change or remove anything another user added unless Ahmed says so; only the build thread bumps versions and publishes; every publish ships whatever is merged, including forms.

## Releases

### 0.4.80 (2026-11-28, Engine's ETABS copy runs minimised, part of the urgent run fix; everything else stays on hold): PR 365
The Engine run's program window is minimised on the taskbar, no longer hidden through the API. On 0.4.79 ETABS had still not started the analysis of Ahmed's real model 8 minutes after opening it; his own visible ETABS takes about 3 minutes. His own run's LOG builds the same model as the Engine's copy (129,925 against 129,831 joints, 763,754 against 762,914 equations), so the copy is doing the same work, only more slowly. Open: a timed run of a small model, then of his model, both against ETABS's own time.

### 0.4.79 (2026-11-28, Engine run speed fix on Ahmed's direct ask; everything else stays on hold): PR 362, 364
Engine run time on Ahmed's real model (0.4.77: 22 minutes inside ETABS before its analysis began, 13:43:51 to 14:05:58, against about 3 minutes in his own ETABS; the whole run went past 40 minutes against his 17). The run window no longer searches ETABS's whole window tree every second for its status bar: the bar is found once and its parts are read every 5 seconds, and the program's windows are looked at every 3 seconds (its log still every second). Each of those looks is answered on ETABS's own thread, the one building the analysis model. Also seen: Ahmed ran the same model in his own ETABS at the same time, so two 14-thread analyses shared the PC. Open: a timed Engine run on the PC with nothing else running.

### 0.4.78 (2026-11-28, SAFE 20.1 tester fixes on Ahmed's direct ask; everything else stays on hold): PR 360
SAFE 20.1 tester field test on 0.4.76 (2026-11-28 12:00, SAFE alone: 1652 passed, 8 failed, 4 not tested), on Ahmed's ask. The .f2k writes a section SAFE has no shape for (Concrete Trapezoid, Nonprismatic, SD Section) with its real size, never 0 by 0 (CPM-TRAP stopped the build). The API build of a .f2k notes a refused or sizeless section and goes on; a refusal names SAFE, not SAP2000; a frame refused by its ends goes in by two points, else the message names the stories, heights and units. The blank SAFE model's story takes the file's height as well as its level (SAFE 20.1 kept height 1, so columns from Z = 0 were refused), and the model is worked in and saved in the file's units (the engineer's lb, in model came back as kip, in). The pack takes the program's own summaries (Frame/Area/Joint Assignments - Summary, section summaries, material lists, SAFE's slab design span data) in all three programs. SAFE's own tendon elongation tables are not compared as model tables. Open: SAFE 20.1 still took only part of the Modeler's .f2k through its tables (18 of 35 objects) and why is not known; the fixes above make the API build that follows it work. Unproven until the tester's next SAFE 20.1 run.

### 0.4.77 (2026-11-28, Engine run fix for real models; everything else stays on hold): PR 358
Engine runs name the program's copy after the engineer's file ("FIXED 05-CHECK COLUMNS-00 1 (CSI ProMax run).EDB", ScratchProgram.RunMark; the run's window checks take it as their own, as cpm-field-test ones), clearing the copy an earlier run left. The run window reads only a multi-line text box of the program's run window as its log (ETABS's hidden form held "C:\Safe\MyModel.fdb", which showed as the log). Open: on Ahmed's real model ETABS sat 7+ minutes busy before writing any analysis file (building the analysis model), on 0.4.75 and 0.4.76.

### 0.4.76 (2026-11-28, on Ahmed's direct ask; everything else stays on hold): PR 355
Engine run window shows what is happening (Ahmed, 2026-11-28; ETABS, SAP2000 and SAFE): the message box shows the program's own run window as it shows it (ETABS's "Analyzing" window title and log box, read off the screen every second, hidden or not), else its .LOG, and before either the Engine's own steps and the windows the program has open; Engine steps starts open and stays as left; Show program window brings the program up through Windows (the API does not answer during an analysis, and the 30 s wait used to give the run's program up) and it stays shown for the rest of the run; File Name is the engineer's own file, with the working copy (cpm-field-test-…) on a line of its own. Open: on Ahmed's 0.4.75 run ETABS wrote no .LOG in 4 minutes; the next run's window will show what ETABS was doing.

### 0.4.75 (2026-11-28, published on Ahmed's order; everything else on hold after it)
ETABS forms: Display > Show Tables (325); Section Designer with every shape and the Interaction Surface (327, 337, 338); BRB and Section Designer sections written back to .e2k exactly as ETABS wrote them (326); Link/Support Properties, all 15 link types, directional and hysteresis forms, with EtabsLinkRoundTrip (328, 334, 340); Analyze and Draw menus, Analyzing window, Analysis Messages, Lock Model (332); Load Combinations, Auto Construction Sequence, Walking and Floor Vibrations, Named Sets, with EtabsDefineRoundTrip (333); Detailing drawing format and sheet setup (343); Options, Select and View menus (344); Steel Connection Design, Live Load Reduction, displacement and period targets, with EtabsDesignRoundTrip (346); File menu item for item with working exports (347). Detailing menu with Bar Selection and Curtailment Rule forms (345); Edit menu forms (348); Design menu, View/Revise Preferences and Tools menu (349); Edit > Add to Model from Template forms (354). Instability check names objects by label and story (350). Real-model carry and table-put fixes (351); field test failure pictures and comparison fixes (352). Field test: pack timing and lost-table count fixed (330); scratch copy held as "(Untitled)" reopened (331); reference data items time-limited (336); the test ends its own blank-model ETABS (342).
Held open, not in 0.4.75: PR 341 (3-D grid lattice, New Model extents), 339 (colour forms, panel drag), 329 (Modeler view windows float and dock).

### 0.4.74 (23:20, 2026-11-27)
ETABS: Materials and all 37 Frame Section shapes (277); Define forms: rebar sizes, groups, section cuts, mass source, P-Delta, generalized displacements, shell load sets (312); Define > Functions (315); Load Patterns with 30 seismic and 27 wind codes, Load Cases (316); Assign and Display menus with every Assign form (317); Nonlinear Hinges with EtabsHingeRoundTrip (319); Display result forms (320); Springs, Soil Profiles, Footings, Panel Zone, BRB (321). SAP2000: Assign tail, Area/Cable/Tendon/Solid/Link forms, Auto Hinges, every Assign > Loads form (299-301, 304, 306, 308, 310, 311, 314). Instability checker solves only active DOF (309). SAFE built through tables when it holds "(Untitled)" (313). Double-clicking a model file opens it in the open Engine (303). Engine runs write .cpme/.cpms/.cpmf (302). ~LLRF lines left out (307).

### 0.4.73 (21:35)
Per-program model files .cpme/.cpms/.cpmf and the Engine asks which program at start (291); ETABS carry step: what the .e2k can't hold goes back in through ETABS's tables (289); SAP2000 Assign Joint and Frame forms (292, 297); real-model round trip in one read session (293); SAFE form tables (294); SAP2000 merge keeps joint names (295); `--fieldtest-stop` and run file named after the model (298); progress.txt in the real-model step (290).

### 0.4.72 (20:55)
Scratch ETABS .e2k open fix, the 5 FAILs of the 0.4.70 full test (287); SAP2000 Define, links, hinges (272), functions (286), Assign tables (280); ETABS to SAFE writes SAFE's own defaults (285).

### 0.4.71 (20:37)
SAFE round-trip reference data gathering (284); SAP2000 .s2k lossless (271); SAFE .f2k lossless (273); large-model pack helpers (274); ETABS .e2k lossless (275); real-model difference files (276); instability checker on real models (278); .f2k carries every definition (279); SAP2000 Load Patterns (281); `--version` (282).

### 0.4.70 (19:46)
.e2k ETABS accepts (263); Display Options and New Model (264); large tables read in pieces (265); run results complete, 6-minute large-model limit (266); real models round trip (267); SAFE .f2k import through tables (268).

### 0.4.69 (18:10)
Pack zipped after the hidden copy closes (257); hourly update check with no window open (258); cleanup: PdfPig removed, package 34.7 to 28.0 MB (259); SAFE .f2k through tables, out of memory never loses the report (262).

### 0.4.68 (17:06)
Everything through PR 256: Engine forms (240, 243), file pickers and units (249, 251), SAFE fixes (252), test-build switch (253), pushover curve (254), large-model frame stresses per load case (255).
