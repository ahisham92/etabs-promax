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
   - the Modeler: a test building made with every Modeler tool, each change read back, written as .e2k, .s2k and .f2k;
   - each CSI program on the PC (ETABS, SAP2000, SAFE): a second window that the test opens itself on that test
     model, saved as a scratch model in the test's own folder. Every tool of that program is run there with each
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

## ETABS, for the SAFE file format (needs licensed ETABS 23)

9. **Compare with ETABS's own SAFE export.** Open `etabs-promax/test-2-plain-frame.e2k` (project files) in ETABS,
   then File > Export > Story as SAFE V12/14/16 .F2K text file, for one story. Send that .f2k file. It is compared
   with what the Modeler's `save_safe_file` writes for the same story, and the table and field names are made to
   match.
