using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Tools;
using EtabsProMax.Bridge;

namespace EtabsProMax.Host;

/// <summary>
/// The 3D view of the open model in the PC's browser: the Viewer page, served on 127.0.0.1 by the chat's own server
/// under a secret address. In the Modeler it follows the model as the chat builds it. It only reads.
/// </summary>
internal static class ModelView
{
    public static void Attach(McpHttpServer server) => server.ViewHandler = Handle;

    private static int Revision(IEtabsLink link) => link is OfflineEtabs offline ? offline.Revision : 0;

    private static JsonObject Content(IEtabsLink link) => Pack.Build(link, [], [], [], null, null, withTables: false).Content;

    internal static (byte[] Body, string ContentType)? Handle(string path, string query)
    {
        if (App.Etabs?.Link is not { } link) return null;
        var revision = Revision(link);
        switch (path)
        {
            case "":
                return (Encoding.UTF8.GetBytes(Pack.LiveViewerHtml(Content(link), revision)), "text/html; charset=utf-8");
            case "model":
                if (query == "rev=" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) return ([], "application/json");
                return (Encoding.UTF8.GetBytes(new JsonObject { ["rev"] = revision, ["pack"] = Content(link) }.ToJsonString()), "application/json");
            default:
                return null;
        }
    }

    /// <summary>Opens the view in the default browser.</summary>
    public static void Open()
    {
        var server = ChatMcp.Ensure() ?? throw new InvalidOperationException("The view could not start on this PC (no local port).");
        Process.Start(new ProcessStartInfo(server.ViewUrl) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Starts the Modeler in a window of its own: the same program, with no ETABS behind it.</summary>
    public static void StartModeler()
    {
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("This program's own path is not known.");
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EtabsProMax.dll"));
        info.ArgumentList.Add("--modeler");
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// A shortcut on the desktop that starts the Modeler with no ETABS: a .lnk to the PC's own dotnet with
    /// EtabsProMax.dll --modeler (no .exe of ours). Made through Windows' own shortcut object.
    /// </summary>
    public static string MakeModelerShortcut()
    {
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("This program's own path is not known.");
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ETABS ProMax Modeler.lnk");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows' shortcut maker is not available on this PC.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(file);
            link.TargetPath = dotnet;
            link.Arguments = "\"" + Path.Combine(AppContext.BaseDirectory, "EtabsProMax.dll") + "\" --modeler";
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.WindowStyle = 7; // minimised: dotnet's own console window stays out of the way
            link.Description = "ETABS ProMax Modeler: build ETABS models with no ETABS open";
            var icon = Path.Combine(AppContext.BaseDirectory, "etabs-promax.ico");
            if (File.Exists(icon)) link.IconLocation = icon;
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        return file;
    }
}
