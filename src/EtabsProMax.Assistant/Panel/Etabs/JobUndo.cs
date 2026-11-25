namespace EtabsProMax.ETABS;

/// <summary>
/// "Undo all" of a chat task. Revit undoes a task as one transaction group; ETABS's API has no undo, so here the card
/// never offers it. What a task changed is counted from its tools' answers instead (Changed), for the card's summary.
/// The safety net in ETABS is the approval before each change and Autopilot's backup of the model file.
/// </summary>
internal static class JobUndo
{
    private static bool _job;
    private static int _changed;

    public static int TaskNumber { get; private set; }

#pragma warning disable CS0067 // never raised: ETABS has no undo
    public static event Action? Undone;
#pragma warning restore CS0067

    /// <summary>How many objects this task's tools reported adding or changing.</summary>
    public static int ElementsChanged => _changed;

    public static void BeginJob()
    {
        _job = true;
        _changed = 0;
        TaskNumber++;
    }

    public static void EndJob() => _job = false;

    public static void BeginTool() { }

    public static void EndTool() { }

    /// <summary>A tool of the running task changed <paramref name="count"/> objects.</summary>
    public static void Changed(int count)
    {
        if (_job && count > 0) _changed += count;
    }

    public static bool CanUndo(out string why)
    {
        why = "ETABS can't undo changes made through its API. Use ETABS's File > Open on the last saved model, or the Autopilot backup, to go back.";
        return false;
    }

    public static string? Undo(EtabsApp app, int task) =>
        "ETABS can't undo changes made through its API. Open the last saved model, or the Autopilot backup beside it, to go back.";
}
