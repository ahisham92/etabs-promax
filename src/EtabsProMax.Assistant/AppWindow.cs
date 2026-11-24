using System.Diagnostics;

namespace EtabsProMax.Assistant;

/// <summary>
/// Opens the chat as a slim window of its own, with no address bar or tabs: Microsoft Edge's app mode, which every
/// Windows 10 and 11 PC has. Without Edge it opens in the default browser.
/// </summary>
public static class AppWindow
{
    public static void Open(string url)
    {
        try
        {
            var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(f => Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe"))
                .FirstOrDefault(File.Exists);
            if (edge is not null)
            {
                // Its own profile folder, so it opens as a separate window whatever Edge is doing.
                var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", "window");
                Process.Start(new ProcessStartInfo(edge, $"--app=\"{url}\" --window-size=460,900 --user-data-dir=\"{profile}\" --no-first-run --disable-features=Translate")
                { UseShellExecute = false })?.Dispose();
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine("The window could not be opened. Open this address in a browser: " + url);
        }
    }
}
