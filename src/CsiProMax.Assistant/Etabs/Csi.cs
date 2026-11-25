using CsiProMax.Assistant.Etabs;

namespace CsiProMax;

/// <summary>
/// The CSI program this CSI ProMax window works beside, for every text the engineer or the chat reads. CSI ProMax runs
/// in ETABS, SAFE and SAP2000, so no shared text may say "ETABS": it says <see cref="Name"/>, which is the program the
/// window was started from. A tool that has the link at hand says the link's own program (ICsiLink.Program.Name),
/// which is also right for the field test's scratch window of another program.
///
/// The rule is checked by a test that reads the source (the texts name the running program): a string that says
/// "ETABS" outside the few ETABS-only files fails it, unless its line ends with the comment "// ETABS itself".
/// </summary>
public static class Csi
{
    /// <summary>Set once, first thing at start (Program.Main), before any text is made. ETABS until then.</summary>
    public static CsiProgram Program { get; set; } = CsiProgram.Etabs;

    /// <summary>"ETABS", "SAFE" or "SAP2000".</summary>
    public static string Name => Program.Name;

    /// <summary>The name with its article: "an ETABS", "a SAFE", "a SAP2000".</summary>
    public static string AName => (Vowel ? "an " : "a ") + Name;

    /// <summary>The same at the start of a sentence: "An ETABS", "A SAFE".</summary>
    public static string ANameCap => (Vowel ? "An " : "A ") + Name;

    private static bool Vowel => Name.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(Name[0]));

    /// <summary>
    /// A fixed text that was written about ETABS (the prompt library, a help page), in the running program's words.
    /// Only for texts that are fixed when the program is built: a text with a file or object name in it must be
    /// written with <see cref="Name"/> instead, or a name that holds "ETABS" would be changed too.
    /// </summary>
    public static string Words(string fixedText) =>
        Program.Key == CsiProgram.Etabs.Key ? fixedText : fixedText.Replace("ETABS", Name, StringComparison.Ordinal);
}
