# Every result and design in the Viewer

Ahmed, 2026-11-25: "all results should be seen either results or design whatever the program allows its API or not.
I need to see the same way I see in the CSI program. Make sure there is no tiny gap." And: "the curves of drift and
movement ... make sure they're covered, and make sure temperature and these things are well covered."

## How nothing is left out

1. **The pack takes every result table, by rule, not by list** (`Tools/PackTables.cs`). The program's table list says
   which tables cannot be imported: those are its Analysis Results and Design tables. Every one with data goes into the
   pack, so a table a newer ETABS, SAP2000 or SAFE version adds is taken too. Beside them go the design and load inputs
   a reviewer checks results against: design preferences and overwrites, load cases, combinations, patterns, mass
   source, spectrum and time history functions, and the load assignments (temperature loads among them). A pack keeps
   at most 1.5 million rows of these; a table that did not fit is named in the pack and in Help > Results Coverage.
2. **Time history steps.** Tables give envelopes by default. The pack sets the program's table output to step by step
   (`DatabaseTables.SetTableOutputOptionsForDisplay`), reads base reactions, story forces and drifts, center of mass
   displacements, accelerations, joint displacements and reactions and link forces again as "... (Step by Step)", then
   sets the output back as it was.
3. **Data the API does not give.** Design tables the API leaves out (shear wall pier and spandrel design, PR #23) come
   from the program's database tables, then the API's summaries, then the program's own Excel or text export saved
   beside the model. The same route works for any result: export the table to Excel next to the model.
4. **The Viewer shows any number of any table.** The dedicated displays below cover what the programs draw; Display >
   Table Values on Model and Display > Plot Functions take any table, so nothing in a pack is only a table.

## ETABS, SAP2000 and SAFE displays, and where each is in the Viewer

| In the CSI program | In the Viewer | From |
|---|---|---|
| Display > Deformed Shape, every load case and combination (dead, live, wind, seismic, temperature, spectrum, time history envelope, nonlinear) | Display > Deformed Shape | Joint Displacements |
| Display > Mode Shape, with each mode's period | Display > Mode Shapes (title shows T) | Joint Displacements (mode rows), Modal Periods And Frequencies |
| Display > Force/Stress Diagrams > Support/Spring Reactions | Display > Force/Stress Diagrams > Support/Spring Reactions | Joint Reactions |
| Frame/Pier/Spandrel/Link Forces: frame P V2 V3 T M2 M3 | Frame Forces and Moments (diagrams, values) | Element Forces - Beams/Columns/Braces |
| Pier and spandrel forces | Pier and Spandrel Forces (on the walls of each pier and spandrel) | Pier Forces, Spandrel Forces |
| Link forces and deformations | Link Forces and Deformations | Element Forces - Links, link deformation tables |
| Spring forces, soil pressures (SAFE, mat foundations) | Spring Forces and Soil Pressures; SAFE: colour by soil pressure | spring and soil pressure tables |
| Shell Stresses/Forces: F11 F22 F12 FMax FMin FVM M11 M22 M12 MMax MMin V13 V23 VMax, S11 S22 S12 SVM ... | Shell Forces/Stresses (contours averaged at joints) | Element Forces/Stresses - Area Shells (SAFE slabs, SAP2000 planes and solids alike) |
| Hinge results, section cut forces, tendon and cable forces | Hinge Results, Section Cut Forces, Tendon and Cable Results | their tables |
| Joint displacement contours | Joint Displacement Contours | Joint Displacements |
| Story Response Plots: max story displacement, drifts, story shears, overturning moments, axial, torsion, stiffness, accelerations, max/avg drifts | Display > Story Response Plots (same display types, X and Y lines, top and bottom of each story) | Story Max Over Avg Displacements, Story Drifts, Story Forces, Story Stiffness, Story Accelerations, Diaphragm Center Of Mass Displacements, Story Max Over Avg Drifts |
| Plot Functions (time history traces), energy | Display > Plot Functions | the "(Step by Step)" tables |
| Static Pushover Curve | Display > Static Pushover Curve | pushover curve tables |
| Response Spectrum Curves; function graphs | Display > Response Spectrum Curves | spectrum tables, Functions tables |
| Modal periods, frequencies, participating mass ratios, load participation | Display > Modal Results Plot, Display > Tables | Modal tables |
| Base reactions, story forces, centers of mass and rigidity, mass summary, torsional irregularity, material lists | Display > Tables, and Story Response Plots where they are by story | their tables |
| Design > Display Design Info, every design kind (steel, concrete, composite beam and column, joist, connection, shear wall pier and spandrel, slab strips, punching) | Design menu (ratios, reinforcing, values), Summary of Design Results | design tables (PR #23) |
| Design details of a member, overwrites, preferences | an object's information window (double click): Design tab shows every design row and its overwrites; Results tab shows every result row about it | design tables, overwrites, preferences |
| Display > Show Tables | Display > Tables (grouped: analysis results, design results, design and load inputs, model definition) | every table of the pack |

Help is in the Viewer itself: Display > Results Coverage lists each kind above and whether the open pack holds it.

## Field test

`Field/ViewerCheck.cs` now chooses every item of the Display and Design menus on the pack from each program (ETABS,
SAP2000, SAFE), applies each window with OK, and fails a step whose view draws nothing or whose page reports an error.
A menu item added later is tested by the same rule. Items greyed out for the pack (no such results) are "not tested".

## Not verified on a real program yet

- The names of the step by step output parameters of `SetTableOutputOptionsForDisplay` in ETABS 23, SAP2000 26 and
  SAFE: the pack looks for names with History, Multistep, NonlinearStatic or Staged and sets them to 2 (step by step).
  If the program refuses, the pack says so in its notes and keeps the envelopes.
- The joint column of ETABS's shell force table (the Viewer accepts Joint, Point, ElemJoint, Node and global X Y Z, and
  falls back to the shell's own corners).
