namespace EtabsProMax.Update;

/// <summary>
/// Sketch ProMax also ships a connector program for the Claude desktop app. ETABS ProMax ships no program of its own (a
/// company PC blocks them), so there is none: the chat reaches the ETABS tools through its own connection inside this
/// window (Bridge/ChatMcp).
/// </summary>
public static class ClaudeRegistration
{
    public static string ConnectorPath => "";
}
