using System.IO;

namespace CsiProMax.Tools;

/// <summary>
/// The hand-offs tools throw (<see cref="ManualStepException"/>) when only the user can unblock them, worded once here
/// so every tool says the same thing: what to do, where in ETABS, why, and how Continue knows it is done.
/// </summary>
internal static class ManualSteps
{
    /// <summary>A file the call names (a DWG, an image) is not there.</summary>
    public static ManualStepException MissingFile(string path, string what) => new(new HandoffStep
    {
        Kind = "missing_file",
        What = $"Put the {what} at {path}, or check the name and folder",
        Where = "Windows File Explorer (copy it there, or connect the network drive it is on)",
        Why = $"The {what} '{Path.GetFileName(path)}' was not found at that path, so it can't be brought into {Csi.Name}.",
        Recheck = () => File.Exists(path),
    });
}
