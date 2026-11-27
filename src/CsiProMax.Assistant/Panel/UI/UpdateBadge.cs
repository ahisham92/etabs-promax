namespace CsiProMax.UI;

/// <summary>
/// The Update button's badge and what clicking it does. Pure: the caller gathers the facts (the running update, a newer
/// version seen or downloaded, restart note, last check, the version whose notes were last opened), so the tests check
/// every case. Each text says only what is known: "Up to date" only after a check that worked, with its time.
/// The button is large, so the texts are two short lines.
/// </summary>
internal static class UpdateBadge
{
    public enum Kind { Working, Ready, Restart, WhatsNew, Failed, Check, UpToDate }

    /// <summary>
    /// How long a successful check counts as "up to date". The update loop checks hourly, so an older check means none
    /// has run lately (ETABS just started, or the network is off): the badge then offers a check instead.
    /// </summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(3);

    /// <param name="working">A short line while an update is being looked for, downloaded or unpacked; else null.</param>
    /// <param name="ready">A newer version that is out, downloaded, or waiting to be switched to; else null.</param>
    /// <param name="restart">New buttons or pane parts need an ETABS restart.</param>
    /// <param name="seen">The version whose release notes were last opened (or that was running the first time); null if unknown.</param>
    /// <param name="lastError">Why the last check failed, or null when it worked (or none ran).</param>
    public static (string Text, Kind Kind) For(string? working, string? ready, bool restart, Version current, Version? seen,
        DateTime? lastCheck, string? lastError, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(working)) return (working!.Trim(), Kind.Working);
        if (!string.IsNullOrWhiteSpace(ready)) return ($"Update ready\n{ready!.Trim()}", Kind.Ready);
        if (restart) return ($"Restart\n{Csi.Name}", Kind.Restart);
        if (seen is not null && Plain(current) > Plain(seen)) return ($"Updated to\n{Plain(current)}", Kind.WhatsNew);
        if (lastError is not null) return ("Couldn't\ncheck", Kind.Failed);
        if (lastCheck is not { } last || now - last > Fresh || last > now.AddMinutes(5)) return ("Check for\nupdates", Kind.Check);
        return ($"Up to date\n(checked {Clock(last)})", Kind.UpToDate);
    }

    /// <summary>
    /// The button at the top of the window: shown only while a newer version is known and not running yet. Its text
    /// names that version; null when this is the newest known (no button). One click installs it and the window
    /// switches to it by itself, with the CSI program left open.
    /// </summary>
    public static string? TopButton(Version current, string? latest, string? downloaded)
    {
        Version? Newer(string? text) => Version.TryParse(text, out var v) && Plain(v) > Plain(current) ? Plain(v) : null;
        var out_ = Newer(latest);
        var ready = Newer(downloaded);
        if (out_ is null && ready is null) return null;
        return ready is not null && (out_ is null || ready >= out_) ? $"Switch to {ready} now" : $"Update to {out_}";
    }

    /// <summary>The tooltip for a badge; <paramref name="describe"/> is the updater's one-line state.</summary>
    public static string Tip(Kind kind, string version, string? lastError, DateTime? lastCheck, string describe, string? restartNote, string? working) =>
        kind switch
        {
            Kind.Working => working ?? describe,
            Kind.Ready => $"{describe} Click to update now.",
            Kind.Restart => $"CSI ProMax {version}. {restartNote}",
            Kind.WhatsNew => $"CSI ProMax {version} is installed. Click to check for a newer one.",
            Kind.Failed => $"CSI ProMax {version}. The update check {(lastCheck is { } at ? "at " + Clock(at) + " " : "")}didn't work: " +
                           $"{(lastError ?? "").Trim().TrimEnd('.')}. Click to try again.",
            Kind.Check => lastCheck is { } at
                ? $"CSI ProMax {version}. Not checked for updates since {Clock(at)} on {Local(at):d MMM}. Click to check now."
                : $"CSI ProMax {version}. Not checked for updates yet. Click to check now.",
            _ => $"{describe} Click to check now.",
        };

    /// <summary>
    /// True when a check <paramref name="every"/> apart is due: none yet, the last one at least that old by the wall
    /// clock, or the clock went back past it.
    /// </summary>
    public static bool CheckDue(DateTime? last, TimeSpan every, DateTime now) =>
        last is not { } l || now - l >= every || l > now + TimeSpan.FromMinutes(5);

    /// <summary>The local time of day of a UTC time, "14:05".</summary>
    public static string Clock(DateTime utc) => Local(utc).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime Local(DateTime utc) => utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
