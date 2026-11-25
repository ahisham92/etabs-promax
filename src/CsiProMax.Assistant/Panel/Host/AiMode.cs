using System.IO;

namespace CsiProMax;

/// <summary>
/// Whether CSI ProMax runs with its AI chat on this PC (Ahmed, 25 Nov 2026: "what if i need to work without AI can someone
/// install just the package without AI"). With it off, the window has no chat, no Claude Code is looked for, offered or
/// started, and the Viewer page has no Chat tab; the Modeler, hand modelling, the 3D view, review packs, the Viewer, the
/// field test and live updates all work the same.
///
/// The choice is one word in %LOCALAPPDATA%\CsiProMax\ai.txt ("on" or "off"): the one-line installer writes it
/// (install.ps1 -NoAI, or install-no-ai.ps1), the CSI ProMax menu switches it, and every version reads it, so an update
/// keeps it. No file means on, as before. --no-ai on the command line turns it off for that window only.
/// </summary>
internal static class AiMode
{
    /// <summary>The choice's file. Tests point it elsewhere.</summary>
    internal static string File { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "ai.txt");

    /// <summary>True when the AI chat is on for this window (read once, at start).</summary>
    public static bool On { get; private set; } = true;

    /// <summary>Started with --no-ai: off for this window, whatever the file says, and the file is left alone.</summary>
    public static bool Forced { get; private set; }

    /// <summary>What the file's text means: only a plain "off" (or no, false, 0) turns the chat off.</summary>
    internal static bool Means(string? text) =>
        (text ?? "").Trim().ToLowerInvariant() is not ("off" or "no" or "false" or "0" or "no-ai" or "noai");

    /// <summary>The choice on this PC as the file says it (on when there is no file or it can't be read).</summary>
    public static bool Saved()
    {
        try { return !System.IO.File.Exists(File) || Means(System.IO.File.ReadAllText(File)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Reads the choice for this window. Call once, before anything is started.</summary>
    public static void Load(string[] args)
    {
        Forced = args.Contains("--no-ai");
        On = !Forced && Saved();
    }

    /// <summary>Saves the choice for this PC (the window then starts again with it). Throws when the file can't be written.</summary>
    public static void Save(bool on)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, on ? "on" : "off");
    }

    /// <summary>For tests: sets the mode of this process without a file.</summary>
    internal static void Use(bool on) => On = on;
}
