using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Bridge;

namespace CsiProMax.Host;

/// <summary>
/// The 3D view of the open model in the PC's browser: the Viewer page, served on 127.0.0.1 by the chat's own server
/// under a secret address. In the Modeler it follows the model as the chat builds it. It only reads.
/// </summary>
internal static class ModelView
{
    public static void Attach(McpHttpServer server) => server.ViewHandler = Handle;

    private static int Revision(ICsiLink link) => link is OfflineEtabs offline ? offline.Revision : 0;

    /// <summary>The Modeler's own tables come along (they cost nothing); a CSI program's result tables do not.</summary>
    private static readonly string[] ModelerTables = ["Grid Definitions", "Frame Assignments - Section Properties", "Area Assignments - Section Properties", "Model Checks", "Quantities"];

    private static JsonObject Content(ICsiLink link) => link is OfflineEtabs
        ? Pack.Build(link, ModelerTables, [], [], null, null).Content
        : Pack.Build(link, [], [], [], null, null, withTables: false).Content;

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
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CsiProMax.dll"));
        info.ArgumentList.Add("--modeler");
        Process.Start(info)?.Dispose();
    }
}
