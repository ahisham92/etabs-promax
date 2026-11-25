using System.Diagnostics;
using EtabsProMax.Tools;

namespace EtabsProMax.ETABS;

/// <summary>
/// Times ETABS ProMax's background work on ETABS's thread (what runs at idle moments and after each change) and writes
/// slow runs and a 10-minute total to the activity log (see <see cref="UiCostTally"/>). Call only on ETABS's thread.
/// </summary>
internal static class UiCost
{
    private static readonly UiCostTally Tally = new(DateTime.Now);

    public static long Start() => Stopwatch.GetTimestamp();

    public static void End(string what, long start)
    {
        try
        {
            var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            var now = DateTime.Now;
            if (Tally.Add(what, ms, now) is { } slow) ActivityLog.Note("etabs_time_slow", slow);
            if (Tally.Due(now) is { } total) ActivityLog.Note("etabs_time_used", total);
        }
        catch (Exception) { /* measuring is best effort */ }
    }
}
