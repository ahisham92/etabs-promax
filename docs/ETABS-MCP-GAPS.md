# CSI ProMax and etabs-mcp: the gap list

[etabs-mcp](https://mcpservers.org/servers/moha2005269/etabs-mcp) (Moha2005269, Apache-2.0, Python) offers 71 ETABS
tools. This is each of them beside what CSI ProMax has. Where CSI ProMax had nothing, a tool was added in C# for all
three programs where their APIs allow it (`src/CsiProMax.Assistant/Tools/CsiTools.cs`, on the call-by-name layer
`Tools/ApiCall.cs`). No code of etabs-mcp is used; its tool list was the checklist.

Columns: **E** ETABS, **S** SAP2000, **F** SAFE. "had" was there before, "new" is added now, "-" is not offered in
that program (and why).

| etabs-mcp | CSI ProMax | E | S | F |
|---|---|---|---|---|
| etabs_list_installations | `list_programs` | new | new | new |
| etabs_connect, etabs_status, etabs_disconnect | the plug-in attaches to the window it was started from (Tools > CSI ProMax) | had | had | had |
| etabs_instances | each window has its own CSI ProMax; `list_programs` counts the open windows | had | had | had |
| etabs_model_overview | `get_model_context` | had | had | had |
| etabs_new_model | `generate_building` (new model), Modeler `new_model` | new | new | new |
| etabs_open_model | Modeler `open_model_file`; `restore_checkpoint` opens a checkpoint | new | new | new |
| etabs_save_model | `save_model` | had | had | had |
| etabs_set_units | `set_units` | new | new | new |
| etabs_get_stories | `get_model_context` | had | - (no stories) | had |
| etabs_set_stories | `set_stories` (Modeler `add_stories`) | new | - (no stories) | - |
| etabs_add_frames | `add_columns`, `add_beams`, `add_frames` (3D, braces) | new | had | new |
| etabs_add_areas | `add_slabs`, `add_walls`, `add_areas` | had | had | had |
| etabs_add_points | `add_joints` | new | had | new |
| etabs_list_objects, etabs_find_objects | `query_objects`, `query_joints` | had | had | had |
| etabs_delete_objects | `delete_objects`, `delete_joints` | new | had | new |
| etabs_set_restraints | `set_supports`, `set_restraints` | had | had | had |
| etabs_assign_section | `set_section` | had | had | had |
| etabs_select | `select_objects` | had | had | had |
| etabs_list_properties | `list_properties` (in full: moduli, strengths, shapes and sizes, thicknesses) | new | new | new |
| etabs_define_material | `add_material` | new | had | new |
| etabs_define_frame_section | `add_frame_section` | new | had | new |
| etabs_define_area_section | `add_area_section` (slab, wall), SAFE `add_slab_section` | new | had | had |
| etabs_define_load_patterns | `add_load_pattern` | had | had | had |
| etabs_assign_loads | `set_loads`, `set_member_loads`, `set_joint_loads`, SAFE `add_point_loads` | new | had | new |
| etabs_define_combinations | `add_combination` | had | had | had |
| etabs_list_load_definitions | `list_load_definitions` (types, cases with factors, mass sources) | new | new | new |
| etabs_define_seismic_load | `add_seismic_load` (ASCE 7-16 ELF or user coefficient) | new | new | - (no lateral loads) |
| etabs_define_wind_load | `add_wind_load` (ASCE 7-16, 7-10 when older) | new | new | - |
| etabs_define_response_spectrum | `add_response_spectrum` (ASCE 7-16 function as points, the case, g Ie / R) | new | new | - |
| etabs_set_mass_source | `set_mass_source` | new | new | - |
| etabs_assign_diaphragm | `assign_diaphragm` (floors in ETABS, joint constraints in SAP2000) | new | new | - |
| etabs_set_modal_case | `set_modal_case` (eigen or Ritz) | new | new | - |
| etabs_generate_building | `generate_building` | new | new | new (one slab on supports) |
| etabs_run_analysis | `run_analysis` (always asks) | had | had | had |
| etabs_analysis_status | `analysis_status` | new | new | new |
| etabs_unlock_model | `unlock_model` (always asks) | new | new | new |
| etabs_get_results | `read_table`, `results_overview`, `joint_displacements`, `frame_forces`, `base_reactions`, `modal_results` | new | had | new |
| etabs_run_design | `run_design` | had | had | had |
| etabs_get_design_summary | `design_ratios` | had | had | had |
| etabs_design_codes | `design_codes` (read and set) | new | new | new |
| etabs_get_concrete_design, etabs_get_wall_design, etabs_get_slab_design | `design_ratios`, the review pack's design results (piers, spandrels), SAFE `slab_design`, `check_punching` | had | had | had |
| etabs_run_shear_wall_design | `run_design` with `shear_wall` | had | - | - |
| etabs_code_checks | `check_report`, `check_drift`, `check_torsion`, `check_story_irregularity`, `check_modal` | had | had | had |
| etabs_scale_response_spectrum | `scale_response_spectrum` (12.9.1.4; compare, then apply) | new | new | - |
| etabs_job_status, etabs_job_wait | the chat's jobs (`chat_jobs`) | had | had | had |
| etabs_list_tables, etabs_get_table | `list_tables`, `read_table` | had | had | had |
| etabs_edit_table | `edit_table` (fields, read, add, update, delete), SAFE also `edit_safe_table` | new | new | new |
| etabs_render_view, etabs_screenshot | `capture_view_image`, the review pack's Viewer | had | had | had |
| etabs_show_window | the CSI ProMax window sits beside the program | had | had | had |
| etabs_refresh_view | `refresh_view` | new | new | new |
| etabs_what_changed | `what_changed` (also changes made by hand) | new | new | new |
| etabs_checkpoint, etabs_list_checkpoints, etabs_restore_checkpoint | `save_checkpoint`, `list_checkpoints`, `restore_checkpoint` (copies of the model file); `model_snapshot` and `compare_snapshots` for the data | new | new | new |
| etabs_export_model | Modeler `save_model_as` (.e2k), `save_model_s2k`, `save_safe_file`; `export_pack` | had | had | had |
| etabs_design_report | `check_report`, `make_document`, `export_pack` | had | had | had |
| etabs_assign_label | `assign_group`; `assign_pier_spandrel` | new | new (groups) | new (groups) |
| etabs_menu, etabs_dialogs | not added: a menu or dialog pressed through the screen can't be undone. The field test answers the program's own notices; `run_etabs_code` and the manual steps cover the rest | - | - | - |
| etabs_search_api | `search_api` | new | new | new |
| etabs_describe_api | `describe_api` | new | new | new |
| etabs_call_api, etabs_call_api_batch | `call_api` (reads, one or many), `run_etabs_code` (changes, the engineer sees the code first) | new | new | new |

"new" in the E and F columns of `add_joints`, `add_frames`, `add_material`, `add_frame_section`, `set_joint_loads`,
`set_member_loads`, `set_releases`, `delete_joints`, `joint_displacements`, `frame_forces`, `base_reactions` and (E)
`modal_results` means SAP2000's tool, which calls the shared API, is now given to ETABS and SAFE too, in their words.

## Tested

- `tests/CsiProMax.Tests/CsiToolsTests.cs`: each tool on a stand-in API that keeps every call (argument names, the
  programs' own direction numbers, the spectrum's points and scale, the scaling arithmetic, table edits, checkpoints,
  the generated building's counts).
- The field test (`src/CsiProMax.Assistant/Field/CsiFieldSteps.cs`): every new tool on each program's scratch model,
  each change read back, a second analysis with the seismic loads and the spectrum, then the spectrum scaled, a
  checkpoint saved and restored, and a building generated. The coverage check of each program fails if one is left out.

Not verified in a real program yet: the argument names of `AutoSeismic.SetASCE716`, `AutoWind.SetASCE716` and
`SourceMass.SetMassSource` in each program (they are given by name, so a different order does not matter, but a
different name falls back to its default), and `Story.SetStories_2` on a model with objects.
