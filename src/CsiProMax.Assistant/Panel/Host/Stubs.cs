using CsiProMax.Tools;

namespace CsiProMax.ETABS;

// Sketch ProMax's bridge calls a few Revit-only helpers. ETABS has no worksharing, no transactions and no failure
// dialogs raised inside API calls, so here each one does nothing, with the same shape, and the bridge code is unchanged.

/// <summary>Synchronize with central: ETABS has no central model, so nothing is ever due.</summary>
internal static class AutoSync
{
    public static bool MaybeDue() => false;
    public static void Seen(EtabsDoc doc) { }
    public static void InCallNow(bool inCall) { }
    public static ToolResult Noted(ToolResult result) => result;
    public static string? RunDue(EtabsApp app) => null;
}

/// <summary>Revit's failure dialogs during a transaction: ETABS's API returns a number instead, which each tool reads.</summary>
internal static class FailureGuard
{
    public static ToolResult Run(Func<ToolResult> work) => work();
}

/// <summary>Time saved is not part of CSI ProMax; the calls stay so the bridge and the chat read the same.</summary>
internal static class TimeLog
{
    public static void BeginTool(EtabsApp app, IEtabsTool tool, bool fromChat) { }
    public static void EndTool(ToolResult? result) { }
    public static void BeginChat(string brief) { }
    public static string? EndChat(int changed, DateTime? askedAt, int jobs) => null;
}

/// <summary>
/// Revit's journal tells which command holds Revit. ETABS keeps no such journal, so nothing is known here: the wait for
/// a busy ETABS says only that it is busy.
/// </summary>
internal static class FreezeWatch
{
    public static FreezeCause.Busy Cause() => new("", null, null, null, false, null);
    public static FreezeCause.Activity Activity() => new(default, 0, 0, 0, 0, TimeSpan.Zero);
    public static void EndLeftCommand() { }
    public static string? ModelToReopen => Host.ModelWatch.Current?.Path;
}

