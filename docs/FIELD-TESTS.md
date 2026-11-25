# Field tests

Checks that need a licensed CSI program, to run on a PC that has one. Each line says what to do and what should
happen. Mark each as passed or failed, and note what you saw when it fails.

## Start here (ETABS, SAP2000 and SAFE)

The field test is inside CSI ProMax. It checks what it can by itself and saves one report file to send back.

1. **Install.** With the CSI program closed, paste this line into Windows PowerShell:
   `irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1 | iex`
   Expected: it ends with a line per program, such as "ETABS 23  CSI ProMax is added to the Tools menu."
2. **Tools menu.** Open the program and a model. Expected: Tools > CSI ProMax is there and opens the CSI ProMax window
   beside the program. If the menu does not show it, add it by hand (the install line prints the plug-in name, menu
   text and file) and say so: that tells us the automatic entry needs fixing.
3. **Run the field test.** In the CSI ProMax window either type "run the field test" in the chat, or choose
   Help > Field test (saves a report to send). It only reads the open model. Run it once on a model that has been
   analysed and designed, so the result checks have something to read.
   Expected: a message with the count of checks that passed, and a file
   `Documents\CSI ProMax\Bugs\Field tests\Field test <program> <date>.md`. Send that file.
4. **Field test with changes, on a scratch model only.** Open a new or throw-away model, then Help > Field test with
   changes (scratch model only). It asks first. It adds a test column, a slab, a load pattern, loads and a combination,
   and deletes the column and slab again. Expected: every check `ok`. Send that report too. Never on a project model.
5. **One zip instead of single files.** Help > Export chats and bugs makes one zip with the chats, the bug reports
   and the field test reports of the period. Send it when more than one thing went wrong.

What the report holds: the versions on the PC, the plug-in lists of the CSI programs, every check with its answer, and
for ETABS the name, fields and first rows of each result table the checks read. The folder of the model is left out.
Without the window: `dotnet CsiProMax.dll --program etabs --fieldtest` (or `sap2000`, `safe`) prints the same report.

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

## ETABS, for the SAFE file format (needs licensed ETABS 23)

9. **Compare with ETABS's own SAFE export.** Open `etabs-promax/test-2-plain-frame.e2k` (project files) in ETABS,
   then File > Export > Story as SAFE V12/14/16 .F2K text file, for one story. Send that .f2k file. It is compared
   with what the Modeler's `save_safe_file` writes for the same story, and the table and field names are made to
   match.
