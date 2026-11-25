using System.Diagnostics;
using System.IO;
using System.Text;

namespace CsiProMax.Host;

/// <summary>
/// How CSI ProMax shows its web pages (the Modeler's 3D view, the 3D view beside a CSI program, the Viewer): in a
/// CSI ProMax window of its own (WebWindow, the Edge WebView2 that Windows 10 and 11 have), never in a browser tab.
/// Only when this PC has no WebView2 Runtime does a page open in Edge's app window (no tabs, no address bar), and
/// only with neither in the default browser. No .exe of ours: the window is CsiProMax.dll itself.
/// Nothing here touches WPF, so the tests and the field test reach it on any machine.
/// </summary>
internal static class WebPages
{
    /// <summary>What one page is: its window title, and whether the window opens maximised (the Modeler and the Viewer are the program).</summary>
    internal enum Kind { ModelerView, ProgramView, Viewer }

    internal static string Title(Kind kind, string program) => kind switch
    {
        Kind.ModelerView => "CSI ProMax Modeler",
        Kind.ProgramView => $"CSI ProMax: 3D view beside {program}",
        _ => "CSI ProMax Viewer",
    };

    internal static bool Maximised(Kind kind) => kind != Kind.ProgramView;

    /// <summary>How a page was shown, for the field test and the error notes.</summary>
    internal enum Way { Window, EdgeApp, Browser }

    /// <summary>WebView2's own files (cache, settings) for CSI ProMax: in the user's folder, never beside dotnet.exe in Program Files.</summary>
    internal static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "WebView2");

    /// <summary>
    /// Whether the window lets <paramref name="target"/> load in place of <paramref name="page"/>: the same local
    /// server (the 3D view's own addresses), or the same file (the Viewer, a review pack's page). Anything else, a link
    /// in a comment for one, opens in the browser so that the CSI ProMax window only ever shows CSI ProMax.
    /// </summary>
    internal static bool StaysInWindow(Uri page, Uri target)
    {
        if (target.Scheme is "about" or "data" or "blob") return true;
        if (page.IsFile || target.IsFile)
            return page.IsFile && target.IsFile && string.Equals(page.LocalPath, target.LocalPath, StringComparison.OrdinalIgnoreCase);
        return string.Equals(page.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(page.Host, target.Host, StringComparison.OrdinalIgnoreCase)
               && page.Port == target.Port;
    }

    /// <summary>Edge's app window: the page alone, with no tabs and no address bar. Null when this PC has no Edge.</summary>
    internal static string? Edge()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            var file = Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>Edge's arguments for an app window of <paramref name="url"/>, with CSI ProMax's own profile so the user's tabs stay apart.</summary>
    internal static IReadOnlyList<string> EdgeAppArguments(string url) =>
        ["--app=" + url, "--user-data-dir=" + Path.Combine(DataFolder, "edge-app"), "--no-first-run"];

    /// <summary>The fallback when the CSI ProMax window cannot be made on this PC: Edge's app window, else the default browser.</summary>
    internal static Way OpenOutside(string url)
    {
        if (Edge() is { } edge)
        {
            try
            {
                var info = new ProcessStartInfo(edge) { UseShellExecute = false };
                foreach (var argument in EdgeAppArguments(url)) info.ArgumentList.Add(argument);
                Process.Start(info)?.Dispose();
                return Way.EdgeApp;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return Way.Browser;
    }

    /// <summary>A link that leaves the window (not CSI ProMax's page): the default browser, as any program does.</summary>
    internal static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>True for a page CSI ProMax wrote: the Viewer, or a review pack's Viewer page (its title says so).</summary>
    internal static bool IsViewerPage(string path)
    {
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[2048];
            var read = stream.Read(head, 0, head.Length);
            return Encoding.UTF8.GetString(head, 0, read).Contains("<title>CSI ProMax Viewer</title>", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The page the Viewer window shows for <paramref name="file"/> (from --viewer, or a pack double-clicked): a Viewer
    /// page is shown as it is; a pack (.cpmx, .zip or a pack saved as .json) is put inside the Viewer page first, in
    /// CSI ProMax's own folder, so it opens at once with no File > Open. No file: the empty Viewer page.
    /// </summary>
    internal static string ViewerPageFor(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return Shortcuts.WriteViewerPage();
        if (IsViewerPage(file)) return Path.GetFullPath(file);
        var bytes = File.ReadAllBytes(file);
        System.Text.Json.Nodes.JsonObject content;
        if (bytes.Length > 1 && bytes[0] == 'P' && bytes[1] == 'K') content = CsiProMax.Assistant.Tools.Pack.Read(bytes);
        else if (System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿')) is System.Text.Json.Nodes.JsonObject json && json["model"] is not null) content = json;
        else throw new InvalidDataException(Path.GetFileName(file) + " is not a CSI ProMax review pack.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "Viewer");
        Directory.CreateDirectory(folder);
        var page = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + " viewer.html");
        File.WriteAllText(page, CsiProMax.Assistant.Tools.Pack.ViewerHtml(content), new UTF8Encoding(false));
        return page;
    }

    /// <summary>The address of a page file, as the window loads it.</summary>
    internal static string Address(string pageOrUrl) =>
        pageOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pageOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? pageOrUrl
            : new Uri(Path.GetFullPath(pageOrUrl)).AbsoluteUri;

    /// <summary>
    /// The window, when it is made (WebWindow sets it at start on Windows): shows a page in a CSI ProMax window and
    /// gives back how. Null off Windows and in the tests.
    /// </summary>
    internal static Func<string, Kind, Way>? Show { get; set; }

    /// <summary>Shows <paramref name="pageOrUrl"/> in its CSI ProMax window, or outside it when no window can be made here.</summary>
    internal static Way Open(string pageOrUrl, Kind kind)
    {
        var address = Address(pageOrUrl);
        if (Show is { } show) return show(address, kind);
        return OpenOutside(address);
    }
}
