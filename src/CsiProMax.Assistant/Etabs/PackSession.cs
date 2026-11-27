using CsiProMax.Assistant.Sap2000;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// A stretch of reading in which the model does not change (writing a review pack and the checks that go in it): the
/// link lists the program's tables once and reads each table once. ETABS, SAP2000 and SAFE all go through
/// <see cref="ComEtabs"/>, so one session serves the three; a stand-in link has nothing to keep.
/// </summary>
public static class PackSession
{
    private sealed class Nothing : IDisposable { public void Dispose() { } }

    private static ComEtabs? Com(ICsiLink link) => link switch
    {
        ComEtabs c => c,
        SapLink { Inner: var inner } => Com(inner),
        _ => null,
    };

    /// <summary>Starts a session on the link; dispose it when the reading is done.</summary>
    public static IDisposable Start(ICsiLink link) => Com(link)?.StartSession() ?? new Nothing();

    /// <summary>The program's table output was changed (step by step): what was read before is read again.</summary>
    public static void Forget(ICsiLink link) => Com(link)?.Forget();
}
