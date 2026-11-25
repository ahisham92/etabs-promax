
namespace CsiProMax.Bugs;

/// <summary>
/// The folders where CSI ProMax keeps what a person makes and what goes wrong on this PC (Help > Open folders), so
/// they can be opened, or copied from a licensed colleague's PC, without knowing the paths. The paths are spelled out
/// like ChatStore's, so this builds without ETABS (unit tests); a test checks they match the folders the add-in uses.
/// </summary>
internal static class UserFolders
{
    internal sealed record Folder(string Title, string Path, string Tip);

    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string Roaming => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsiProMax");
    private static string Local => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax");

    public static IReadOnlyList<Folder> List() =>
    [
        new("Bugs", BugLog.Root, "Report bug zips (Reports), Export chats & bugs zips (Exports) and the bug log."),
        new("Learned tools", System.IO.Path.Combine(Roaming, "learned-tools"), "The tools saved from the chat, one .json file each."),
        new("Recipes", System.IO.Path.Combine(Roaming, "recipes"), "Saved recipes: steps the chat can run again."),
        new("Chats", System.IO.Path.Combine(Local, "chat", "conversations"), "The saved chats of every model."),
        new("Made in chat", System.IO.Path.Combine(Documents, "CSI ProMax", "Made in chat"), "The Excel, Word, PowerPoint, PDF and CAD files the chat made for you."),
        new("Downloaded versions", System.IO.Path.Combine(Local, "versions"), "Updates of CSI ProMax, one folder each; the newest is used when the window opens."),
        new("Settings", Roaming, "Settings, license and everything above that is saved per user."),
    ];
}
