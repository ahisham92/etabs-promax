namespace EtabsProMax.Assistant;

/// <summary>
/// Remembers the window's address while the program runs, in the user's own folder, so that starting it a second time
/// (the ETABS menu again) shows the window that is already there instead of starting another.
/// </summary>
public static class Running
{
    private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", "running.txt");

    /// <summary>The address of a copy that is running and answers, or null.</summary>
    public static string? Find()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            var window = System.IO.File.ReadAllText(File).Trim();
            if (!Uri.TryCreate(window, UriKind.Absolute, out var uri) || !uri.IsLoopback) return null;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var answer = http.GetAsync(new Uri(uri, "/api/ping" + uri.Query)).GetAwaiter().GetResult();
            return answer.IsSuccessStatusCode && answer.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("etabs-promax") ? window : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException) { return null; }
    }

    public static void Write(string window)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Clear()
    {
        try { System.IO.File.Delete(File); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
