namespace CsiProMax.Assistant;

/// <summary>
/// The field test's rule for "not tested" (Ahmed, 2026-11-26: "make sure the tests are covering everything as the SAFE
/// test why it was not covering everything from the start"). A step may be left out only for a reason the test cannot
/// work around: the program or its licence is not on this PC, the step needs a person, a file dialog or a printer, the
/// program's own API has no such call, this is not Windows, it would change the engineer's own model, or the PC is set
/// up without the part. Everything else that used to be listed as "not tested" (the scratch model has nothing for it,
/// an earlier step did not work, a table could not be read back) is a gap in the test or a bug, and is a failure.
/// </summary>
internal static class SkipAudit
{
    public enum Why { ProgramNotHere, Licence, NotWindows, PcSetUpWithoutIt, NeedsAPerson, ProgramCannot, YourOwnModel, WindowWentAway, NoWindowInThisRun, Unjustified }

    /// <summary>The reasons that hold, by a part of their text, each with its kind. A new reason is added here or it fails.</summary>
    internal static readonly (string Part, Why Kind)[] Known =
    [
        ("licence", Why.Licence), ("license", Why.Licence),
        ("was not found on this PC", Why.ProgramNotHere), ("API file was not found", Why.ProgramNotHere),
        ("has kept a plug-in list", Why.ProgramNotHere), ("the stand-in has no", Why.ProgramNotHere),
        ("no CSI program is behind", Why.ProgramNotHere), ("no program is behind this window", Why.ProgramNotHere),
        ("this run has no CSI program behind it", Why.ProgramNotHere), ("could not be tried on this PC for a program on", Why.ProgramNotHere),
        ("was not opened from a CSI program", Why.ProgramNotHere), ("there is no scratch model to change", Why.ProgramNotHere),
        ("not Windows", Why.NotWindows), ("Windows only", Why.NotWindows), ("needs Windows", Why.NotWindows),
        ("no WebView2 Runtime", Why.PcSetUpWithoutIt), ("is not beside CsiProMax.dll", Why.PcSetUpWithoutIt),
        ("no installed copy was found", Why.PcSetUpWithoutIt), ("no CSI ProMax Engine shortcut", Why.PcSetUpWithoutIt),
        ("shortcut reader is not available", Why.PcSetUpWithoutIt), ("no GitHub sign-in", Why.PcSetUpWithoutIt),
        ("team library is switched off", Why.PcSetUpWithoutIt), ("no Claude Code was found", Why.PcSetUpWithoutIt),
        ("the AI chat is off on this PC", Why.PcSetUpWithoutIt), ("the AI chat is on in this window", Why.PcSetUpWithoutIt),
        ("does not say whether it is signed in", Why.PcSetUpWithoutIt), ("no Edge or Chrome was found", Why.PcSetUpWithoutIt),
        ("CSI ProMax is not in this list", Why.PcSetUpWithoutIt),
        ("a file or a printer", Why.NeedsAPerson), ("needs the chat pane", Why.NeedsAPerson), ("needs a chat that is running", Why.NeedsAPerson),
        ("from the phone is being answered", Why.NeedsAPerson), ("asks you on your phone", Why.NeedsAPerson),
        // His own open CSI ProMax window is never closed by the test, like his own model.
        ("is already open on this PC", Why.YourOwnModel), ("a Modeler is open on this PC", Why.YourOwnModel),
        ("API has no call", Why.ProgramCannot),
        ("cannot be set through this ETABS's API", Why.ProgramCannot), // ETABS itself
        ("API cannot draw design strips", Why.ProgramCannot), ("opens only its own", Why.ProgramCannot),
        ("never on your open model", Why.YourOwnModel), ("the open model has no", Why.YourOwnModel),
        ("lists no table with data in this model", Why.YourOwnModel), ("the model has not been analysed", Why.YourOwnModel),
        ("the open model could not be read", Why.YourOwnModel),
        ("stopped answering", Why.WindowWentAway), ("no longer answers", Why.WindowWentAway),
        ("this run has no CSI ProMax window", Why.NoWindowInThisRun),
    ];

    /// <summary>The kind of a "not tested" reason; <see cref="Why.Unjustified"/> when it is none of the reasons that hold.</summary>
    public static Why Of(string why)
    {
        foreach (var (part, kind) in Known)
            if (why.Contains(part, StringComparison.OrdinalIgnoreCase)) return kind;
        return Why.Unjustified;
    }

    /// <summary>
    /// Whether a step may be left out for this reason. Ahmed, 2026-11-26: "you can skip as long as no license and if the
    /// program is not installed but as long as it is there do detailed testing for everything": the program (or a part
    /// of the install, or Windows itself) is not there, or its licence is not. Two more are kept because the test must
    /// not do otherwise: his own open model is never changed or analysed, and steps after a window that went away are
    /// tried again in the next run (the window going away is itself a failure). A step that needs a person, a file
    /// dialog or a printer, that the program's API cannot do, is not
    /// left out: it is tested another way, and until it is, it fails.
    /// </summary>
    public static bool Allowed(Why kind) => kind is Why.ProgramNotHere or Why.Licence or Why.NotWindows or Why.PcSetUpWithoutIt or Why.YourOwnModel or Why.WindowWentAway or Why.NoWindowInThisRun;

    /// <summary>What the report says for a step left out without a reason that holds.</summary>
    public static string Failure(string why) =>
        $"not tested, and that is not allowed here ({Words(Of(why))}), so it counts as failed: {why}" +
        " (a step may be left out only when the program or its licence is not on this PC; anything else is tested another way)";

    /// <summary>In plain words, for the report's list of what was not tested.</summary>
    public static string Words(Why kind) => kind switch
    {
        Why.ProgramNotHere => "the program is not on this PC, or no program is behind this run",
        Why.Licence => "the program's licence",
        Why.NotWindows => "Windows only",
        Why.PcSetUpWithoutIt => "this PC is set up without it",
        Why.NeedsAPerson => "needs a person, a file dialog or a printer",
        Why.ProgramCannot => "the program's own API cannot do it",
        Why.YourOwnModel => "your own model and your own open windows are never changed, analysed or closed by the test",
        Why.WindowWentAway => "the program's window went away (tried again in the next run)",
        Why.NoWindowInThisRun => "this run has no CSI ProMax window (Help > Field test in the window covers it)",
        _ => "no reason that holds",
    };
}
