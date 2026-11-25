# Field tests

Checks that need a licensed CSI program, to run on a PC that has one. Each line says what to do and what should
happen. Mark each as passed or failed, and note what you saw when it fails.

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
7. **Review pack.** Ask for `export_pack`. Open the Viewer page in a browser. Expected: the header says SAFE; the
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
