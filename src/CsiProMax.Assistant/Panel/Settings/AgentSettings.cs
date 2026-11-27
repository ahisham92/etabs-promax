using System.IO;
using System.Text.Json;

namespace CsiProMax.Settings;

/// <summary>
/// Per-user settings stored in %APPDATA%\CsiProMax\settings.json, edited from the ribbon's Settings button.
/// The MCP server reads the same file, so firm standards reach Claude in the desktop app.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>
    /// The old on/off switch, kept only so an older CSI ProMax reading this file still behaves: written as true unless
    /// the level is "Ask only for risky changes". <see cref="ApprovalLevel"/> is what counts.
    /// </summary>
    public bool RequireApprovalForChanges { get; set; } = true;

    /// <summary>
    /// How often ETABS asks before Claude changes something: risky_only (the default), once_per_task, every_change or
    /// always_allow.
    /// Empty in files written before the levels, which read as the default (Tools.ApprovalPolicy.Parse).
    /// </summary>
    public string? ApprovalLevel { get; set; }

    /// <summary>Tools the user said not to ask about again ("Don't ask again" in the dialog); cleared in Settings.</summary>
    public List<string>? NoAskTools { get; set; }

    /// <summary>The approval level as an enum.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Tools.ApprovalLevel Approvals
    {
        get => Tools.ApprovalPolicy.Parse(ApprovalLevel);
        set
        {
            ApprovalLevel = Tools.ApprovalPolicy.Store(value);
            RequireApprovalForChanges = value is not (Tools.ApprovalLevel.RiskyOnly or Tools.ApprovalLevel.AlwaysAllow);
        }
    }

    /// <summary>Firm standards in plain English (naming, sheet numbering, tag styles...), given to Claude.</summary>
    public string FirmStandards { get; set; } = "";

    /// <summary>
    /// Where Claude Code is on this PC, when the user pointed at it (the chat's card, Choose the file): claude.exe or
    /// claude.cmd. Looked at before the usual places (Chat.ClaudeFinder). Empty: not set.
    /// </summary>
    public string ClaudePath { get; set; } = "";

    /// <summary>Download new versions from GitHub when ETABS starts; they take effect the next time ETABS opens.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Also take the test builds (pre-releases v0.4.67-testN) before they go to everyone: only on the field test PC.</summary>
    public bool TestBuilds { get; set; }

    /// <summary>
    /// CSI ProMax Engine's Run: leave the CSI program open, shown, with the analysed model after the run, instead of
    /// closing it to free its licence. On for now, until runs are settled (Ahmed 2026-11-27); the next run closes it first.
    /// </summary>
    public bool KeepProgramOpenAfterRun { get; set; } = true;

    /// <summary>How much the chat pane moves: full (the default), reduced or off (Chat.MotionLevels). Empty in older files: full.</summary>
    public string? ChatAnimations { get; set; }

    /// <summary>
    /// How many of a chat's parallel jobs run at once (each is a session on the user's plan): 3 by default, 1 to 6
    /// (Chat.JobBoard). More wait to start until one finishes.
    /// </summary>
    public int ChatJobLimit { get; set; } = 3;

    /// <summary>A job pauses itself after using this percent of the plan's 5-hour limit (Chat.JobBudget; 0: never).</summary>
    public int ChatJobBudget { get; set; } = 15;

    /// <summary>Which finished jobs a teammate checks before they are reported (Chat.JobReview: always, autopilot or off).</summary>
    public string ChatJobReview { get; set; } = "always";

    /// <summary>
    /// Auto sync (ETABS.AutoSyncRule): when Claude's work has made <see cref="AutoSyncChanges"/> changes to a local copy of
    /// a central model since its last sync, every chat job pauses, the model is synchronized with central, and the work
    /// carries on where it stopped; a step bigger than that is split across syncs. On by default.
    /// </summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>After how many changes auto sync synchronizes: 250 by default, 20 to 5000.</summary>
    public int AutoSyncChanges { get; set; } = 250;

    /// <summary>
    /// The names in the large chat's crew strip (Chat.CrewNames): you, the coordinator, the workers (comma separated, one
    /// per job at once) and the one who applies changes in ETABS. Empty: the defaults (you: your Windows account's name).
    /// </summary>
    public string? ChatCrewYou { get; set; }
    public string? ChatCrewCoordinator { get; set; }
    public string? ChatCrewWorkers { get; set; }
    public string? ChatCrewApplier { get; set; }

    /// <summary>
    /// The type a new chat starts as (Chat.ChatTypes): "simple" (one chat does everything itself, the default since
    /// 26 Nov 2026, Ahmed) or "coordinator" (it hands work to parallel jobs). New chat's arrow picks the other type for one
    /// chat; New autopilot chat is always With coordinator.
    /// </summary>
    public string ChatTypeDefault { get; set; } = "simple";

    /// <summary>
    /// The user picked <see cref="ChatTypeDefault"/> in Settings since it became Simple chat by default. Before that every
    /// Save wrote "coordinator", the old default, so a file without this reads as Simple chat (Load).
    /// </summary>
    public bool ChatTypeChosen { get; set; }

    /// <summary>
    /// The model and effort a new chat starts on (Chat.ChatAuto): "auto" (Automatic: picked per request and per job by
    /// the kind of work) unless set here; a choice in the chat's pickers sticks for that chat.
    /// </summary>
    public string ChatModelDefault { get; set; } = "auto";
    public string ChatEffortDefault { get; set; } = "auto";

    /// <summary>What Automatic gives each kind of work (Chat.ChatAuto): simple, everyday and hard; model aliases and effort levels.</summary>
    public string ChatModelSimple { get; set; } = "haiku";
    public string ChatModelEveryday { get; set; } = "sonnet";
    public string ChatModelHard { get; set; } = "opus";
    public string ChatEffortSimple { get; set; } = "low";
    public string ChatEffortEveryday { get; set; } = "medium";
    public string ChatEffortHard { get; set; } = "high";

    /// <summary>The coordinator's own turns in a chat With coordinator (ChatAuto.Coordinating): light, the jobs do the heavy work.</summary>
    public string ChatModelCoordinator { get; set; } = "sonnet";
    public string ChatEffortCoordinator { get; set; } = "low";

    /// <summary>An autopilot paused at a weekly limit carries on by itself when it resets (the 5-hour limit always does).</summary>
    public bool AutopilotResumeWeekly { get; set; } = true;

    /// <summary>During an autopilot, a job pauses after this percent of the 5-hour limit (Chat.JobBudget; 0: never).</summary>
    public int AutopilotJobBudget { get; set; } = 15;

    /// <summary>An autopilot pauses when the 5-hour limit reaches this percent, and carries on when it resets.</summary>
    public int AutopilotFiveHourMax { get; set; } = 90;

    /// <summary>One autopilot run may use this percent of the weekly limit, then pauses (0: no cap of its own).</summary>
    public int AutopilotWeekShare { get; set; } = 25;

    /// <summary>
    /// During an autopilot, a job that needs the user asks them on their phone (Chat.AskUser, Iris) and waits this many
    /// minutes for an answer before carrying on with its assumption (0: nothing is asked).
    /// </summary>
    public int AutopilotAskWaitMinutes { get; set; } = 10;

    /// <summary>
    /// While workers or an autopilot work, a progress update goes to the user's phone this often, in minutes; what they write
    /// back reaches the coordinator (0: none). Named AutopilotUpdateMinutes before 31 Oct 2026, when 0 often stood for "no
    /// hourly email" and left the phone with no updates: the old value is not carried over.
    /// </summary>
    public int PhoneUpdateMinutes { get; set; } = 60;

    /// <summary>
    /// The user's names for projects on the phone, "HBR = Harbour, TWR = Tower" (PhoneProjects.Renamed): each project has
    /// its own chat there, and the name CSI ProMax guesses from the model's file can be changed or two joined (2 Nov 2026).
    /// </summary>
    public string PhoneProjectNames { get; set; } = "";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsiProMax", "settings.json");

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Upgraded(JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    /// <summary>
    /// A file written by an older version, read as this one means it (26 Nov 2026): the old default chat type and the old
    /// default crew names were stored as if chosen; they read as the new defaults (Simple chat; Pitso, Ali, Carmen, Carla,
    /// Dieng). Names the user typed, and a chat type picked since, are kept.
    /// </summary>
    internal static AgentSettings Upgraded(AgentSettings s)
    {
        if (!s.ChatTypeChosen) s.ChatTypeDefault = "simple";
        s.ChatCrewCoordinator = Chat.CrewNames.Stored(s.ChatCrewCoordinator, Chat.CrewNames.DefaultCoordinator, Chat.CrewNames.OldCoordinator);
        s.ChatCrewWorkers = Chat.CrewNames.Stored(s.ChatCrewWorkers, Chat.CrewNames.DefaultWorkers, Chat.CrewNames.OldWorkers);
        s.ChatCrewApplier = Chat.CrewNames.Stored(s.ChatCrewApplier, Chat.CrewNames.DefaultApplier);
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
