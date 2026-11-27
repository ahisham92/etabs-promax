using System.IO;
using System.Text.Json.Nodes;

namespace CsiProMax.Host;

/// <summary>
/// File > Open, Save As, Import and Export in the Engine's window open Windows' own file and folder pickers straight
/// away, as ETABS, SAP2000 and SAFE do (Ahmed 2026-11-27: "open the select file directly", no typed path). The page
/// asks with {"kind":"open"|"save"|"folder","title","filter","name","folder","ext"}; the answer is {"ok":true,"path"}
/// or {"ok":true,"cancelled":true}. The picker belongs to the Engine's window, on its thread.
/// </summary>
internal static class FilePicker
{
    /// <summary>Where a picker starts when the page names no folder: Documents\CSI ProMax\Models when it is there.</summary>
    public static string StartFolder(string? asked)
    {
        if (!string.IsNullOrWhiteSpace(asked) && Directory.Exists(asked)) return asked;
        var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CSI ProMax", "Models");
        return Directory.Exists(models) ? models : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>The request's kind, checked: open, save or folder.</summary>
    public static string Kind(JsonNode? request) => (request?["kind"]?.GetValue<string>() ?? "open") switch { "save" => "save", "folder" => "folder", "probe" => "probe", _ => "open" };

    /// <summary>A picker filter as Windows takes it ("Label|*.a;*.b|..."), from the page's, or every file.</summary>
    public static string Filter(JsonNode? request)
    {
        var f = request?["filter"]?.GetValue<string>() ?? "";
        return f.Split('|').Length % 2 == 0 && f.Length > 0 ? f : "All files|*.*";
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static async Task<JsonObject> ShowAsync(byte[] body)
    {
        JsonNode? request;
        try { request = JsonNode.Parse(body.Length == 0 ? "{}"u8.ToArray() : body); }
        catch (System.Text.Json.JsonException) { request = null; }
        // The page asks once whether this window shows pickers (a plain browser has none), before laying out its forms.
        if (Kind(request) == "probe") return new JsonObject { ["ok"] = true, ["probe"] = true };
        var app = System.Windows.Application.Current;
        if (app is null) return new JsonObject { ["ok"] = false, ["error"] = "no window to show the picker in" };
        return await app.Dispatcher.InvokeAsync(() => Show(request));
    }

    private static JsonObject Show(JsonNode? request)
    {
        var owner = System.Windows.Application.Current.Windows.OfType<WebWindow>().FirstOrDefault(w => w.IsActive)
            ?? System.Windows.Application.Current.Windows.OfType<WebWindow>().FirstOrDefault();
        var title = request?["title"]?.GetValue<string>();
        var folder = StartFolder(request?["folder"]?.GetValue<string>());
        string? path;
        switch (Kind(request))
        {
            case "folder":
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = title ?? "Select Folder", InitialDirectory = folder };
                path = (owner is null ? d.ShowDialog() : d.ShowDialog(owner)) == true ? d.FolderName : null;
                break;
            }
            case "save":
            {
                var ext = (request?["ext"]?.GetValue<string>() ?? "").TrimStart('.');
                var d = new Microsoft.Win32.SaveFileDialog
                {
                    Title = title ?? "Save As", Filter = Filter(request), InitialDirectory = folder, FileName = request?["name"]?.GetValue<string>() ?? "",
                    DefaultExt = ext, AddExtension = ext.Length > 0, OverwritePrompt = true,
                };
                path = (owner is null ? d.ShowDialog() : d.ShowDialog(owner)) == true ? d.FileName : null;
                break;
            }
            default:
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = title ?? "Open", Filter = Filter(request), InitialDirectory = folder, CheckFileExists = true, Multiselect = false };
                path = (owner is null ? d.ShowDialog() : d.ShowDialog(owner)) == true ? d.FileName : null;
                break;
            }
        }
        return path is null ? new JsonObject { ["ok"] = true, ["cancelled"] = true } : new JsonObject { ["ok"] = true, ["path"] = path };
    }
}
