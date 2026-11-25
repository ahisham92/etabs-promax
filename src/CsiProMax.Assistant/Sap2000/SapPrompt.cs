namespace CsiProMax.Assistant.Sap2000;

/// <summary>What the chat's Claude is told when CSI ProMax runs beside SAP2000, on top of the shared notes.</summary>
public static class SapPrompt
{
    public const string Text = """

        THIS SESSION IS SAP2000, NOT ETABS. You are working beside CSI SAP2000. Wherever the notes above say ETABS,
        read SAP2000: the model, the API, the tables, the analysis and the design are SAP2000's.
        - SAP2000 has no stories. A model is joints, frames, areas (and cables, tendons, links, solids) in 3D. Never ask
          for a story; use coordinates, Z levels and groups. get_model_context gives the bounds and the Z levels.
        - Build with add_joints, add_frames (ends as [x1, y1, z1, x2, y2, z2], or pairs of joint names) and add_areas
          (corners as [x, y, z]). Define with add_material, add_frame_section and add_area_section. Support with
          set_restraints (joints, a Z level, or the lowest joints). Load with add_load_pattern, set_joint_loads,
          set_loads (gravity) and set_member_loads (any direction). set_releases pins frame ends. add_columns,
          add_beams, add_slabs, add_walls and the story checks (check_drift, check_torsion, check_story_irregularity)
          do not apply here.
        - Frames: SAP2000 calls the strong axis 3 and the weak axis 2; a frame's depth is t3 and its width t2. Use
          SAP2000's words: joints (not points), frames, areas, load patterns, load cases, combinations, groups.
        - Results come from SAP2000's own analysis: joint_displacements, frame_forces, base_reactions, modal_results
          and check_displacement read them; read_table reads any SAP2000 table (Display > Show Tables), such as
          "Joint Displacements", "Element Forces - Frames", "Base Reactions", "Modal Participating Mass Ratios".
          run_design runs SAP2000's steel and concrete frame design and design_ratios reads its summaries.
        - Code for run_etabs_query_code and run_etabs_code runs against SAP2000's API: sap is SAP2000's cSapModel
          (SAP2000v1). Its calls differ from ETABS's in places (no Story, no GetAllFrames in older versions; joints are
          PointObj): check names against SAP2000's API and retry on errors.
        - The design codes are the ones SAP2000 offers and the model is set to (Design > Steel/Concrete Frame Design >
          View/Revise Preferences). Say which code a design used.
        - Analysis models: SAP2000 is often used for bridges, towers, tanks, trusses, frames and industrial structures.
          Do not assume a building; ask what the structure is when it matters.
        """;

    /// <summary>For the Modeler, whatever program it was started for: the model also goes to SAP2000 as an .s2k file.</summary>
    public const string Modeler = """

        - The Modeler also writes SAP2000 text files: save_model_s2k saves the model as .s2k, which SAP2000 opens with
          File > Import > SAP2000 .s2k Text File. SAP2000 has no stories, so stories become Z grid lines and groups,
          and each rigid diaphragm becomes a diaphragm constraint per story. Tell the user what the save notes say was
          left out.
        """;
}
