# Field tests

Checks that need a licensed CSI program, to run on a PC that has one. Each line says what to do and what should
happen. Mark each as passed or failed, and note what you saw when it fails.

## Start here (ETABS, SAP2000 and SAFE)

The field test is inside CSI ProMax. It checks what it can by itself and saves one report file to send back.

1. **Install.** With the CSI program closed, paste this line into Windows PowerShell:
   `irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1 | iex`
   Expected: it ends with a line per program, such as "ETABS 23  CSI ProMax is added to the Tools menu.", and then
   "Checked: can each program open CSI ProMax?" with "yes" for each program.
2. **Tools menu.** Open the program and a model. Expected: Tools > CSI ProMax is there and opens the CSI ProMax window
   beside the program. If the menu does not show it, add it by hand (the install line prints the plug-in name, menu
   text and file) and say so: that tells us the automatic entry needs fixing. If the program answers "Cannot create
   plugin!", paste `irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/check.ps1 | iex` into Windows
   PowerShell and send back the text it prints.
3. **Run the full field test.** In the CSI ProMax window choose Help > Field test: everything (saves a report to
   send), or type "run the field test" in the chat. It asks first, then runs in the background (up to an hour):
   - this PC and the install: versions, the plug-in files and each program's plug-in list, the folders CSI ProMax
     writes to, the licence, Claude Code and its sign-in;
   - the open model, reading only (run it on a model that has been analysed and designed, so the result checks have
     something to read);
   - the Modeler: a test building made with every Modeler tool, each change read back, written as .e2k, .s2k and .f2k,
     and each of those opened again in the Modeler as the whole model;
   - each CSI program on the PC (ETABS, SAP2000, SAFE): a second window that the test opens itself on that test
     model, saved as a scratch model in the test's own folder (SAFE 20 and 21 open only .fdb files through their API,
     so there the test starts a blank model and builds the Modeler's .f2k in it through the API, all but the design
     strips, which SAFE's API cannot draw). Every tool of that program is run there with each
     change read back through the program's API, then the analysis, the design, every result table and check, the
     snapshots and the review pack. Then the tools every program shares (the whole API searched, described and
     read; units; properties and load definitions in full; table edits; groups and piers; mass source, modal case,
     seismic, wind and response spectrum loads with a second analysis, the spectrum scaled to the ELF base shear;
     unlocking; a checkpoint saved and restored; a building made in one call), each change read back (see
     docs/ETABS-MCP-GAPS.md), and a learned tool made in another program is run there. The window is closed without
     saving at the end;
   - CSI ProMax's own features: every kind of file it writes and reads, the review pack, notes, recipes, learned
     tools, code, the self test, the bug report and the export. What it makes is taken away again;
   - the team library: its sync rules on two made-up PCs, then the real library on GitHub (sign in, write a test
     file in field-test/, read it back, remove it). No GitHub sign-in on the PC is "not tested", with how to sign in.

   Leave the windows it opens alone while it runs, and wait for it before using the chat. Your open model is only
   read, never changed. If a program shows a message of its own in the window the test opened (a licence message,
   say), the test waits a few minutes, then gives up on that program and says so in the report.
   Expected: a message on the screen with the counts (passed, failed, not tested), and two files in
   `Documents\CSI ProMax\Bugs\Field tests`: `Field test <program> <date>.md` (the report) and a `.zip` beside it
   with the report and the small files the test made. **Send the zip.**
4. **Check by hand.** The report ends its first part with a short list of what no test can do (the Tools menu entry,
   an answer in the chat, the picture in a bug report, the pack in a browser). Try each and say which did not work.
5. **The quick test.** Help > Field test, quick: this PC and the open model, or "run the quick field test" in the
   chat. A minute or two, reading only. Use it after an update, or when the full test cannot run.
6. **One zip of everything.** Help > Export chats and bugs makes one zip with the chats, the bug reports and the field
   test reports of the period. Send it when more than one thing went wrong.

What the report holds: "Not working" first (each failure with what was expected and what was found), then "Not
tested" (each with the reason), then the plug-in lists, then every check of every part with its answer. The folder of
the model and the Windows user's folder are left out. Without the window: `dotnet CsiProMax.dll --program etabs
--fieldtest --full` (or `sap2000`, `safe`; leave out `--full` for the quick test) prints the same report.

## Opening SAP2000 and SAFE files in the Modeler (2026-11-26)

The Modeler opens .s2k and .f2k as well as .e2k (File > Open, or `open_model_file`). A file the Modeler wrote carries
the whole model after `END TABLE DATA`, where SAP2000 and SAFE stop reading, and opens exactly as it was saved; a file
SAP2000 or SAFE wrote is read from its tables. The field test checks the first kind. By hand, for the second:

1. In SAP2000 with a model open: File > Export > SAP2000 .s2k Text File, all tables. In the Modeler, File > Open that
   .s2k. Expect the same joints, frames, areas, supports, load patterns, cases, combinations and loads as SAP2000
   shows; members on the level of their top end, levels from the Z grid lines. Anything left out is listed quietly
   in the answer's `not_read` (ask the chat), never as a message.
2. In SAFE 20 or later: File > Export > SAFE .F2K Text File. In the Modeler, File > Open that .f2k. Expect its slabs,
   beams, columns, walls, supports, soil, tendons, design strips, loads, cases (hyperstatic too) and combinations.
3. Open the .s2k the Modeler saved in SAP2000 (File > Import). SAP2000 should import it with no message about the
   lines after END TABLE DATA (UNVERIFIED on a real PC until this is done once).

## Review pack written by itself after every run and design (2026-11-26)

CSI ProMax keeps `<model>.cpmx` and `<model> viewer.html` beside the model up to date with no button: a background
watcher (`dotnet CsiProMax.dll --packs`, no window) looks at each open ETABS, SAP2000 and SAFE window every 10 seconds
and writes the pack again when the model got new results (its `.LOG`/`.OUT` beside the model is newer than the pack,
or the model became analysed) or when a design ran (a design table got data, or the API says the design has results).
It only reads: the model is never saved, unlocked or changed. It is started by the CSI ProMax window and at every
sign-in by the Tools menu keeper; `-NoPacks` on the install line (or `packs-off.txt` in `%LOCALAPPDATA%\CsiProMax`)
turns it off. What it did is in `%LOCALAPPDATA%\CsiProMax\packs.log`, what it watches in `packs.json`.

The full field test checks it on each program's scratch model while it has results (steps "... review pack is written
by itself after its analysis", "... writes nothing again when nothing changed", "... written again after a design,
and keeps the reviewer's comments", "the watcher leaves the field test's own ... model to the field test").

By hand, once per program (ETABS 23, SAP2000 26, SAFE; and ETABS 20 / SAFE 20 after opening Tools > CSI ProMax once):
1. Open a saved model, run the analysis. Expected: within about 30 s of the analysis ending, `<model>.cpmx` and
   `<model> viewer.html` appear beside the model, and `packs.log` says "pack written after the analysis".
2. Open the pack in the CSI ProMax Viewer and leave it open. Run a design (Design > ... > Start Design/Check).
   Expected: `packs.log` says "pack written after the design", and the open Viewer reloads by itself with the design.
3. Run the analysis again without changing anything else. Expected: the pack is written again (after the analysis).
4. From a command line: `dotnet "%USERPROFILE%\Documents\CSI ProMax\CsiProMax.dll" --packs --once` prints what it sees in
   each open program and writes a pack where one is due. Send its text when step 1 did not work.

The numbered checks below are what the field test does by itself, kept as a list to go through by hand when it
cannot run, or to look closer at one that failed.

## ETABS (needs ETABS 18 or later, licensed; 23 is the one built against)

After the three steps above, with a scratch model open:

E1. **Chat, model.** Ask: "What is in this model?" Expected: stories, counts of beams, columns, slabs and walls,
    sections, load patterns and units, in ETABS's words.
E2. **Chat, a change.** Ask: "Add a 300x600 concrete beam section and put a beam between two columns on Story1."
    Expected: asked for approval when "ask before changes" is on, then done, and ETABS shows the beam.
E3. **Chat, what always asks.** Ask to delete that beam, then to run the analysis. Expected: each asks first, every
    time, because ETABS has no undo.
E4. **Checks against ETABS's own tables.** After the analysis, ask: "Check the drift against H/500" and "Check the
    modal mass". Expected: the governing story and load case, pass or fail, and the name of the ETABS table read.
    Compare one number with Display > Show Tables in ETABS. A check that says "not available" while ETABS shows
    that table: the field test report already holds the table names, so send it.
E5. **Check report and review pack.** Ask for the check report, then "export the review pack". Open the Viewer page
    it names. Expected: the model in 3D, the checks, and the results tables; both files (the `.cpmx` and the
    `viewer.html`) are in the model's own folder, beside the `.edb`.
E5b. **Every design result in the Viewer.** On a model with steel, concrete frames and shear walls (piers and
    spandrels labelled), run the analysis and every design in ETABS (Design > Steel Frame, Concrete Frame, Composite
    Beam, Shear Wall Design > Start Design/Check), then "export the review pack". Expected: the chat's answer lists
    where each design came from; in the Viewer, Design menu > each design > Display Design Info shows the D/C
    colours, the concrete reinforcing at the stations, and the pier and spandrel values on the walls, matching ETABS's
    own Display Design Info. Checks tab > Design results gives the largest D/C of each. If a design is missing, export
    the design tables (File > Export > ETABS Tables to Excel) into the model's folder, named after the model, and
    make the pack again: it should then take them from that file. The full field test labels its scratch wall as
    a pier, runs run_design and reports every design table ETABS lists (with data or empty): send that report.
E6. **The window.** Shrink the chat to one line and back, maximise the window, close it and open it again from
    Tools > CSI ProMax. Expected: the chat of this model is still there (Home > history).
E7. **Bug report.** Help > Report a bug, write one line. Expected: a zip in `Documents\CSI ProMax\Bugs\Reports`.
E8. **Update.** Settings > Check for updates now. Expected: "up to date", or a newer version that downloads and is
    used the next time the window opens.

## SAFE (needs SAFE 20 or later, licensed)

Install: run the one-line install, or copy the CSI ProMax folder to Documents. The SAFE plug-in is
`CsiProMaxSafePlugin.dll` in that folder.

1. **Plug-in entry.** In SAFE: Tools > Add/Show Plugins. If CSI ProMax is not listed yet, add
   `Documents\CSI ProMax\CsiProMaxSafePlugin.dll`, plug-in name `CsiProMaxSafePlugin`, menu text `CSI ProMax`.
   Expected: Tools > CSI ProMax appears and opens the CSI ProMax window. The window's title or chat says SAFE.
2. **Attach.** Open a scratch SAFE model (a slab on columns, or a mat), then in PowerShell in the CSI ProMax folder:
   `dotnet CsiProMax.dll --program safe --selftest`
   Expected: every step `ok`, including the four `SAFE ...` steps at the end. Send the whole output: it shows which
   API file was used (SAFEv1.dll or CSiAPIv1.dll) and the names of SAFE's tables.
3. **Changes on a scratch model.** `dotnet CsiProMax.dll --program safe --selftest --changes`
   Expected: every step `ok`. Never on a real project model.
4. **Chat, model.** In the window, ask: "What is in this model?" Expected: units, slabs, supports, loads, said in
   SAFE's words (no drift or story checks offered).
5. **Chat, SAFE tools on a scratch model.** Ask, one at a time:
   - "Define a 600 mm mat slab property" (add_slab_section)
   - "Put soil with subgrade 20000 kN/m3 under all slabs" (add_soil_support)
   - "Add pinned supports at (0,0) and (6,0)" (add_point_supports)
   - "Put a 500 kN dead load at (3,3)" (add_point_loads)
   - "List the design strips" (list_design_strips)
   - "Show the fields of the design strip table" (edit_safe_table, action fields)
   Expected: each is asked for approval when it changes the model, then done, and SAFE shows the change.
6. **Checks.** Run the analysis and the design in SAFE, then ask for `safe_check_report` with an allowable bearing
   pressure and span/240. Expected: punching shear, deflection, soil pressure, strip forces and slab design each
   say pass, fail or reported, and name the SAFE table they read. Any check that says "not available" while SAFE
   has that result: send the table's name from Display > Show Tables.
7. **Review pack.** Ask for `export_pack`. Open the Viewer page in a browser (it is in the model's own folder,
   beside the `.fdb`). Expected: the header says SAFE; the
   Colour list offers Punching ratio and Soil pressure, and the dots sit on the columns and on the mat.
8. **Modeler .f2k into SAFE.** In the Modeler, build or open a small frame and ask to `save_safe_file` for one
   floor (and once with a subgrade for a mat). In SAFE: File > Import > SAFE .F2K file. Expected: slabs, beams,
   column supports, strips, loads and combinations come in. If SAFE reports a table or field it does not know,
   send the import log.
8a. **GeoProMax, the loop on a mat.** On a scratch mat with soil under it, divided into panels of about 1.5 m
   (Edit > Divide Areas), analysed:
   - "Export the soil pressures for GeoProMax" (export_soil_pressures). Expected: a `.csv` beside the `.fdb`, in
     kN and m, with a pressure for every point.
   - In GeoProMax, SAFE link: load the file, pick the service case, work out the springs, save
     "Download for CSI ProMax".
   - "Apply the GeoProMax springs from <path>" (apply_geopromax_springs). Expected: it says the model is locked and
     asks to unlock; then SAFE has soil properties GPM-K1 ... (Define > Soil Subgrade Properties) with the moduli
     of the file in the model's units, and Display shows the zones under the panels as in GeoProMax's zone map.
   - Run again, export again: GeoProMax's next round shows a smaller change. Repeat until it says converged.
   - On a mat drawn as one area: the answer lists it under `mixed`; with "as points" SAFE gets point springs at
     the cell centres and the area's own soil is taken off. Send the answer if SAFE refuses any call.
   The same three steps in ETABS on a model with a raft at the base (areas only).

9. **Older programs (SAFE 20/21, ETABS 18 to 21, SAP2000 20 to 25: they run on the .NET Framework).** Since
   0.4.41 the window reaches them through the plug-in's pipe (Tools > CSI ProMax), and the field test's second
   window through Windows PowerShell. Close the programs and run the one-line install first, so the Tools menu
   has the new plug-in. Then open a model (an untitled one is fine), start CSI ProMax from Tools > CSI ProMax and run
   Help > Field test. Expected: "read the open model" ok, "start a second SAFE window" ok, and the tools that read
   the model (export_pack, run_recipe, run_learned_tool, run_etabs_query_code) ok. "Tools > CSI ProMax in SAFE 20"
   is ok while SAFE 20 itself opened the window, though SAFE.ini does not list it until SAFE closes.
10. **Open a SAFE model from the chat.** Ask "open E:\...\LEVEL 29.FDB" (open_model). Expected: it asks first (the
   model open now is closed), then SAFE has that model and the header names it. An .edb is refused with "open it in
   ETABS". The field test runs it on its scratch model (tool open_model).

## ETABS, for the SAFE file format (needs licensed ETABS 23)

9. **Compare with ETABS's own SAFE export.** Open `etabs-promax/test-2-plain-frame.e2k` (project files) in ETABS,
   then File > Export > Story as SAFE V12/14/16 .F2K text file, for one story. Send that .f2k file. It is compared
   with what the Modeler's `save_safe_file` writes for the same story, and the table and field names are made to
   match.

## Slabs and post-tensioning (2026-11-26)

The format is taken from ETABS 20's and SAFE 20/23's own exports of their "ACI 318-19 PT-SL Ex001" and "RC-PN Ex001"
examples (tests/CsiProMax.Tests/samples/*-v20.e2k, *-v20.f2k, *-v23.f2k). The unit tests read them and write them
back word for word. What only a licensed program can confirm:

11. **Import a Modeler PT model into ETABS.** The field test's Modeler part draws a tendon section, the two prestress
   patterns, a draped tendon jacked from both ends with fixed losses, a hyperstatic case and a strip. Import its
   .e2k (File > Import > ETABS .e2k text file). Expected: no import warnings; Display > Tendons shows the drape;
   Assign > Tendon Loads shows "Both" ends (the e2k word for both ends and for the I end, "Start", is not yet seen in
   an export: if ETABS refuses it, the import log names the line). Run the analysis: the hyperstatic case runs.
12. **Strips with several segments.** A strip through three points writes a middle point with the end widths of
   one segment and the start widths of the next. ETABS's own export of such a strip has not been seen yet.
13. **Import a Modeler .f2k into SAFE 20 or later.** From 2026-11-26 the Modeler's SAFE file is in SAFE 20's own
   tables (POINT/COLUMN/BEAM/FLOOR/WALL/TENDON/STRIP OBJECT CONNECTIVITY, ... with the fields named as SAFE 20.1
   and 23.1 write them), no longer SAFE 16's. Save the field-test model as .f2k from the Modeler, then in SAFE use
   File > Import > SAFE .F2K. Expected: no import warnings; the slab, columns, tendon drape (Display > Tendons),
   strips (layer A and B) and the hyperstatic case are there. Not yet seen in an export, so they may need a fix:
   a tendon of more than one plan segment or span (its further rows), a strip of more than one segment (its
   further rows), "Both Ends" and "I End" for the jack, beams (BEAM OBJECT CONNECTIVITY), walls (WALL OBJECT
   CONNECTIVITY), soil as area springs ("Stiffness Option"="User Specified"), frame and point loads.
14. **Punching, losses, openings and ribs from ETABS 23.2.** Taken from ETABS 23.2's export of PT-SL Ex001
   edited by hand (tests/CsiProMax.Tests/samples/etabs-PT-SL-v23-edited.e2k): detailed losses (CURVCOEFF, WOBBLECOEFF,
   ANCHORSLIP, ELASTICSHORT, CREEPSTRESS, SHRINKSTRESS, STEELRELAX), percent losses (LOSSPERCENTST/LT, now kept as
   percent, not turned into stresses), JACKLOC "Both", a Parabola span, a punching overwrite with a user perimeter
   (PUNCHINGSHEARPERIMETER) and stud rails, an opening (an AREA-type area with OPENING "Yes") and a ribbed slab.
   The Modeler's forms and the chat write the same lines (unit test). Still to confirm in a program:
   - SAFE .f2k: an opening as NULL AREA OBJECT CONNECTIVITY with "Section Property"=Opening; and the punching
     reinforcement's yield stress under "Reinforcement Diameter" and its diameter under "Reinforcement Fy". The
     programs' table keys name the two fields swapped (RebarFy is named "Reinforcement Diameter"), and the file
     follows the keys. If SAFE shows fy 0.012 and diameter 420000, the two names go the other way.
   - An .f2k rebuilt in SAFE 23.1 through the API (the empty-open fallback) leaves openings and punching
     overwrites out and says so in its notes.
15. **Slab line releases and slab rebar.** ETABS 23.2's J export (Ahmed's PC, 2026-11-26) showed that a slab line
   release is not in the .e2k at all. It exists only in the table "Area Assignments - Slab Line Releases": UniqueName
   (the null line), ShearL, MomentL, ShearR and MomentR, each Yes or No, and the API has no call for it. ETABS Plus
   23.2 refused every slab rebar object: both tables answered 1 with an empty log and no licence box. The Modeler
   draws the null line as ETABS writes it (LINE ... LINE, SECTION "NONE") and keeps the release on the line's
   assignment, the same way it keeps the other rows the .e2k has no keyword for. Slab rebar is kept in its own file.
   Both go into the .f2k in SAFE's tables. When CSI ProMax opens the Modeler's .e2k in ETABS, or runs the model there,
   they go in through the program's tables, with the release on the line found where the Modeler drew it. Still to
   confirm: that ETABS takes a release row put in on a null line it just opened, and that SAFE and ETABS Ultimate
   take slab rebar from these tables.