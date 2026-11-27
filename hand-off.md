# CSI ProMax hand-off

Updated with every release (Ahmed, 2026-11-27): what each release did, newest first, and the tasks still open.
Times are Cairo. Every PC takes a release through the live updater (hourly check since 0.4.69); nothing to paste.

## Still to do

1. **ETABS first (Ahmed's order).** Every remaining ETABS 23.2 form and menu identical field by field, each with a field round trip that reads the values back in ETABS. Pending forms are listed by the field test in `field/forms-check.md` on the test PC.
2. **Field test to 0 FAIL.** Rerun only the failed checks on each new release until 0, then one full run. Open on 0.4.73: the scratch copy ETABS holds as "(Untitled)" (fixed by PR 331) and two pack failures (PR 330, pack thread).
3. **Lossless real-model round trip.** ETABS .e2k / SAP2000 .s2k / SAFE .f2k in and out with nothing lost on the real example models; `model table differences.txt` and `result differences.txt` must come back empty.
4. **Results pack timing.** Read, convert and load every result in 3 minutes for small models and 6 minutes for large ones, with no table left out and the PC never locked.
5. **SAP2000 and SAFE (paused, ETABS first).** SAP2000: remaining Define/Analyze/Display forms. SAFE: forms and the tester's SAFE 22.7/23.1 reruns (the reference data gathering stays in every release until the data is received).
6. **After the forms and 0 FAIL.** Close the program after a run (on hold, `KeepProgramOpenAfterRun` is true for now); central model with local copies for several users; side-by-side form pictures for every program.

Standing rules: never change or remove anything another user added unless Ahmed says so; only the build thread bumps versions and publishes; every publish ships whatever is merged, including forms.

## Releases

### 0.4.75
PR 325 ETABS Display > Show Tables (Choose Tables, table window, Table Format). PR 326 BRB and Section Designer sections written back to .e2k exactly as ETABS wrote them. PR 327 ETABS Section Designer (Property Data, the window, shapes, design types). PR 328 ETABS Link/Support Properties, all 15 link types, with EtabsLinkRoundTrip. PR 331 scratch copy held as "(Untitled)" opened again; shear wall design menu fallback. PR 332 ETABS Analyze and Draw menus, Analyzing window, Analysis Messages, Lock Model. PR 333 ETABS Load Combinations, Auto Construction Sequence, Walking and Floor Vibrations, Named Sets, with EtabsDefineRoundTrip.

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
