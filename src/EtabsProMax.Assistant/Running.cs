namespace EtabsProMax.Assistant;

/// <summary>
/// Remembers the window's address while the program runs, in the user's own folder, so that starting it a second time
/// (the ETABS menu again, in the same ETABS window) shows the window that is already there instead of starting another.
/// </summary>
public static class Running
{
    private static string File(int? pid) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", pid is null ? "running.txt" : $"running-{pid}.txt");

    /// <summary>The address of a copy that is running and answers, or null.</summary>
    public static string? Find(int? pid)
    {
        try
        {
            if (!System.IO.File.Exists(File(pid))) return null;
            var window = System.IO.File.ReadAllText(File(pid)).Trim();
            if (!Uri.TryCreate(window, UriKind.Absolute, out var uri) || !uri.IsLoopback) return null;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var answer = http.GetAsync(new Uri(uri, "/api/ping" + uri.Query)).GetAwaiter().GetResult();
            return answer.IsSuccessStatusCode && answer.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("etabs-promax") ? window : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException) { return null; }
    }

    public static void Write(int? pid, string window)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File(pid))!);
            System.IO.File.WriteAllText(File(pid), window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Clear(int? pid)
    {
        try { System.IO.File.Delete(File(pid)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
