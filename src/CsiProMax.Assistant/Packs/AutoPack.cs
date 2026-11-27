using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Packs;

/// <summary>
/// What one look at a program says: the model's file, whether it has results (locked), when its analysis log was last
/// written (null when there is none beside the model), and which design tables have data, as one text.
/// </summary>
public sealed record PackLook(string File, bool Locked, DateTime? RunStamp, string Design);

/// <summary>What the pack beside the model says it was made from (its manifest's "auto"), or what a pack made by hand has.</summary>
public sealed record PackMade(DateTime? RunStamp, string Design);

/// <summary>
/// The review pack beside the model, kept up to date by itself (Ahmed, 2026-11-26: "whenever a run happens it converts
/// the file to the cpmx ... whenever you click design it updates this file"). The programs have no event for a run
/// or a design, so they are looked at: the analysis log beside the model is read from the disk (no call to the
/// program at all), and the program is asked only two cheap things, whether the model has results and which tables
/// have data. The pack is written only when the results or the design changed since it was made. Only reads: the
/// model is never saved, unlocked or changed, and the program's own messages are never answered.
/// </summary>
public static class AutoPack
{
    /// <summary>A run whose log was written less than this long ago may still be running: the next look decides.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether to write the pack now, and why ("analysis" or "design"); null when not. Pure, so the rule is tested
    /// without a program.
    /// </summary>
    /// <param name="before">The look before this one at the same program (null at the first).</param>
    /// <param name="made">The pack beside the model (null when there is none).</param>
    public static string? Decide(PackLook? before, PackLook now, PackMade? made, DateTime nowUtc)
    {
        if (!Saved(now.File) || !now.Locked) return null;
        // The analysis is still writing its log: not yet.
        if (now.RunStamp is { } run && nowUtc - run < Settle && run <= nowUtc.AddMinutes(1)) return null;
        if (made is null) return "analysis";
        var same = before is not null && string.Equals(before.File, now.File, StringComparison.OrdinalIgnoreCase);
        // A new run: its log is newer than the one the pack was made from, or the model has just got its results.
        if (now.RunStamp is { } stamp && (made.RunStamp is not { } had || Math.Abs((stamp - had).TotalSeconds) > 1)) return "analysis";
        if (same && !before!.Locked) return "analysis";
        // A design: tables have data that did not when the pack was made, or the design was deleted and run again
        // (a change of a design overwrite deletes it) while this watched.
        if (now.Design.Length > 0 && now.Design != made.Design) return "design";
        if (same && now.Design.Length > 0 && before!.Design != now.Design) return "design";
        return null;
    }

    /// <summary>A model saved to a file of its own: an unsaved model has no folder to put a pack beside.</summary>
    public static bool Saved(string? file)
    {
        file = (file ?? "").Trim().Trim('"');
        // A Windows path (C:\... or \\server\...) is a saved model wherever this runs; the tests run on Linux too.
        if (System.Text.RegularExpressions.Regex.IsMatch(file, @"^([A-Za-z]:[\\/]|\\\\)[^\\/]+.*[\\/][^\\/]+$|^[A-Za-z]:[\\/][^\\/]+$")) return true;
        try { return file.Length > 0 && Path.IsPathRooted(file) && Path.GetDirectoryName(file) is { Length: > 0 }; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The field test's own models (cpm-...) are left to the field test, which checks this itself.</summary>
    public static bool FieldTestModel(string file) => Path.GetFileName(file.Replace('\\', '/').TrimEnd('/')).StartsWith("cpm-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pack's file beside the model: the model's name with .cpmx, the same file every time.</summary>
    public static string PackFile(string modelFile)
    {
        var file = modelFile.Trim().Trim('"');
        return Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".cpmx");
    }

    /// <summary>The Viewer page beside it, with the pack inside, for a reviewer who has nothing installed.</summary>
    public static string PageFile(string modelFile)
    {
        var file = modelFile.Trim().Trim('"');
        return Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + " viewer.html");
    }

    /// <summary>
    /// When the model's analysis last wrote its log beside the model: ETABS, SAP2000 and SAFE all write
    /// &lt;model&gt;.LOG (and ETABS and SAP2000 &lt;model&gt;.OUT) there when an analysis runs. Null when there is none (the
    /// analysis files are kept in another folder).
    /// </summary>
    public static DateTime? RunStamp(string modelFile)
    {
        if (!Saved(modelFile)) return null;
        try
        {
            var file = modelFile.Trim().Trim('"');
            var folder = Path.GetDirectoryName(file)!;
            var name = Path.GetFileNameWithoutExtension(file);
            DateTime? newest = null;
            foreach (var ext in new[] { ".LOG", ".OUT" })
            {
                var log = Path.Combine(folder, name + ext);
                if (!File.Exists(log)) continue;
                var at = File.GetLastWriteTimeUtc(log);
                if (newest is null || at > newest) newest = at;
            }
            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Which design tables have data, as one text (their keys, sorted): it changes when a design runs or is deleted.</summary>
    public static string DesignOf(IEnumerable<TableName> tables) =>
        string.Join("|", tables.Where(t => !t.Empty && DesignResults.IsDesignTable(t.Name) && !IsDesignInput(t.Name)).Select(t => t.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>Preferences and overwrites have data whether a design ran or not: they say nothing about a run.</summary>
    private static bool IsDesignInput(string table)
    {
        var n = ResultTools.Norm(table);
        return n.Contains("preference", StringComparison.Ordinal) || n.Contains("overwrite", StringComparison.Ordinal) || n.Contains("definition", StringComparison.Ordinal);
    }

    /// <summary>One look at the program: two cheap calls (the model and its tables) and the log's time on the disk.</summary>
    public static PackLook Look(ICsiLink link)
    {
        var info = link.Info();
        var file = (info.File ?? "").Trim().Trim('"');
        var design = "";
        if (info.Locked)
        {
            design = DesignOf(link.Tables());
            // SAP2000 gives some designs only through its API (its tables stay empty): the API is asked too.
            if (ApiDesigns(link) is { Length: > 0 } api) design = design.Length == 0 ? api : design + "|" + api;
        }
        return new PackLook(file, info.Locked, RunStamp(file), design);
    }

    private static readonly string[] DesignApis = ["DesignSteel", "DesignConcrete", "DesignCompositeBeam", "DesignAluminum", "DesignColdFormed"];

    /// <summary>The kinds of design the API says have results now ("api:DesignSteel"...), one cheap call each; a kind the program has no call for is left out.</summary>
    internal static string ApiDesigns(ICsiLink link)
    {
        try
        {
            return link.Raw("look at which designs have results", m =>
            {
                var have = new List<string>();
                foreach (var api in DesignApis)
                    try
                    {
                        dynamic design = api switch
                        {
                            "DesignSteel" => m.DesignSteel, "DesignConcrete" => m.DesignConcrete, "DesignCompositeBeam" => m.DesignCompositeBeam,
                            "DesignAluminum" => m.DesignAluminum, _ => m.DesignColdFormed,
                        };
                        if (Convert.ToBoolean(design.GetResultsAvailable())) have.Add("api:" + api);
                    }
                    catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { /* not in this program */ }
                return string.Join("|", have);
            }) as string ?? "";
        }
        catch (EtabsException) { return ""; }
    }

    /// <summary>What the pack beside the model was made from; null when there is no pack (or it cannot be read: it is made again).</summary>
    public static PackMade? MadeOf(string packFile)
    {
        try
        {
            if (!File.Exists(packFile)) return null;
            using var zip = ZipFile.OpenRead(packFile);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject manifest) return null;
            // A pack made by hand (export_pack) has no "auto": it counts as made before any run, so the next run's
            // pack replaces it, and a design adds to it.
            if (manifest["auto"] is not JsonObject auto) return new PackMade(null, "");
            DateTime? run = auto["run_stamp"]?.GetValue<string>() is { Length: > 0 } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            return new PackMade(run, auto["design"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException) { return null; }
    }

    public sealed record Written(string Pack, string Page, string Why, int Frames, int Areas, int Tables, int Comments, TimeSpan Took, IReadOnlyList<string> Notes);

    /// <summary>
    /// Writes the pack and the Viewer page beside the model, replacing the ones there. A reviewer's comments in the
    /// pack there are carried into the new one. Each file is written beside its place first and then moved in, so a
    /// Viewer that reads it never gets half a file.
    /// </summary>
    /// <summary>Whether a pack after an analysis is read from a copy of the model in a hidden program (<see cref="PackCopy"/>).</summary>
    public static bool ReadFromCopy { get; set; } = true;

    public static Written Write(ICsiLink link, PackLook look, string why)
    {
        var watch = Stopwatch.StartNew();
        var pack = PackFile(look.File);
        var page = PageFile(look.File);
        // The reviewer's comments: those in the pack there now and those kept beside the model when a pack was taken away.
        var comments = KeepComments(look.File);
        var auto = new JsonObject
        {
            ["why"] = why,
            ["run_stamp"] = look.RunStamp?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["design"] = look.Design,
            ["made_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        var by = why == "design" ? "CSI ProMax, by itself after the design" : "CSI ProMax, by itself after the analysis";
        Pack.Built Make(ICsiLink from, bool zip)
        {
            // The checks and the pack read the same tables: each is read from the program once.
            using var session = PackSession.Start(from);
            JsonObject checks;
            try { checks = ProgramTools.Report(JsonDocument.Parse("{}").RootElement, from).AsObject(); }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException or ToolInputException) { checks = new JsonObject { ["note"] = "The checks could not be made: " + ex.Message }; }
            return Pack.Build(from, [], [], [], checks, comments, by: by, instabilities: true, auto: auto, fileAs: look.File, zip: zip);
        }
        Pack.Built? built = null;
        // After an analysis the results are in the saved files: they are read from a copy in a hidden program, so the
        // engineer's window is not held while the pack is read (Ahmed 2026-11-27). A design is only in the engineer's
        // window until it is saved, so it is read there.
        if (why != "design" && ReadFromCopy && FieldTest.IsLive(link))
        {
            // Zipped after the hidden program is closed: it holds a licence only while it reads.
            var copy = PackCopy.Read(link.Program, look.File, from => Make(from, zip: false));
            built = copy.Value;
            auto["read"] = copy.Value is not null ? copy.Said : $"the engineer's {link.Program.Name}, because {copy.Said}";
            auto["copy_seconds"] = Math.Round(copy.Took.TotalSeconds, 1);
        }
        built = built is null ? Make(link, zip: true) : Pack.Zipped(built);
        lock (LockFor(pack)) PackLife.Write(pack, page, built.Zip, built.Content);
        return new Written(pack, page, why, built.Frames, built.Areas, built.Tables.Count, comments.Count, watch.Elapsed, built.Notes);
    }

    /// <summary>The reviewer's comments, kept beside the model for as long as the model is (a pack comes and goes with
    /// the results): PackLife's "&lt;model&gt; comments.json", the same file the Engine keeps.</summary>
    public static string CommentsFile(string modelFile) => PackLife.CommentsFile(PackFile(modelFile));

    /// <summary>The name this rule kept comments under before it shared PackLife's (2026-11-26): read once, then removed.</summary>
    internal static string OldCommentsFile(string modelFile)
    {
        var file = modelFile.Trim().Trim('"');
        return Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + " review comments.json");
    }

    /// <summary>
    /// Every comment of the pack beside the model and of the comments file, each once, written to the comments file
    /// (so no comment is lost when the pack is taken away or made again) and given back.
    /// </summary>
    public static JsonArray KeepComments(string modelFile)
    {
        var all = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Take(JsonArray? list)
        {
            foreach (var c in list ?? [])
                if (c is not null && seen.Add(c["who"]?.ToString() + "\u0001" + c["at"]?.ToString() + "\u0001" + c["text"]?.ToString())) all.Add(c.DeepClone());
        }
        var kept = CommentsFile(modelFile);
        var old = OldCommentsFile(modelFile);
        Take(PackLife.Comments(PackFile(modelFile)));
        try { if (File.Exists(old)) Take(Pack.ReadComments(old).Comments); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        if (all.Count > 0)
        {
            var o = new JsonObject { ["model"] = Path.GetFileNameWithoutExtension(modelFile.Trim().Trim('"')), ["comments"] = all.DeepClone() };
            PackLife.Replace(kept, new UTF8Encoding(false).GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        }
        // The old name's comments are in the shared file now.
        if (File.Exists(old) && File.Exists(kept)) try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return all;
    }

    /// <summary>Whether the pack beside the model is one this rule wrote (a pack made by hand with export_pack is the engineer's and is left alone).</summary>
    public static bool IsAuto(string packFile)
    {
        try
        {
            if (!File.Exists(packFile)) return false;
            using var zip = ZipFile.OpenRead(packFile);
            if (zip.GetEntry("manifest.json") is not { } entry) return false;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd()) is JsonObject manifest && manifest["auto"] is JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return false; }
    }

    /// <summary>
    /// The pack and its Viewer page taken away because the model has no results now (unlocked in the program, or
    /// changed in the Engine): the comments are kept beside the model first, and come back in the next pack. Only a
    /// pack this rule wrote is taken away. False when there was none. A pack open in a program that holds it is not
    /// taken away now: the IOException says so, and the next look tries again.
    /// </summary>
    public static bool Remove(string modelFile)
    {
        var pack = PackFile(modelFile);
        lock (LockFor(pack))
        {
            if (!IsAuto(pack)) return false;
            KeepComments(modelFile);
            // PackLife keeps the comments, tries again while another program holds a file, and says what is left.
            var left = PackLife.Remove(pack, PageFile(modelFile));
            if (File.Exists(pack)) throw new IOException(string.Join("; ", left));
            return true;
        }
    }

    // ---- several windows on one model ----

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Pack, bool Locked, DateTime At)> Windows = new(StringComparer.Ordinal);

    /// <summary>One pack is written or taken away at a time, whichever window asked.</summary>
    internal static object LockFor(string packFile) => Locks.GetOrAdd(Path.GetFullPath(packFile), _ => new object());

    /// <summary>A window's last look, so another window on the same model knows it still has results.</summary>
    internal static void Seen(string window, string packFile, bool locked) => Windows[window] = (packFile, locked, DateTime.UtcNow);

    /// <summary>A window that closed no longer counts.</summary>
    public static void Forget(string window) => Windows.TryRemove(window, out _);

    /// <summary>Another window (looked at in the last few minutes) shows the same model with its results.</summary>
    internal static bool LockedElsewhere(string window, string packFile) =>
        Windows.Any(w => w.Key != window && w.Value.Locked && DateTime.UtcNow - w.Value.At < TimeSpan.FromMinutes(3) && string.Equals(w.Value.Pack, packFile, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One look at one program and, when the rule says so, the pack written. What happened is given back as one line
    /// for the log (null when nothing happened). <paramref name="before"/> is this program's previous look and is
    /// moved on, except when writing failed (the next look tries again).
    /// </summary>
    public static string? Once(ICsiLink link, ref PackLook? before, bool fieldTestModels = false, DateTime? nowUtc = null, string? window = null)
    {
        var now = Look(link);
        if (!fieldTestModels && FieldTestModel(now.File)) { before = now; return null; }
        if (!Saved(now.File)) { before = now; return null; }
        var pack = PackFile(now.File);
        window ??= link.Program.Key + ":" + link.GetHashCode().ToString(CultureInfo.InvariantCulture);
        Seen(window, pack, now.Locked);
        // No results now (unlocked to change the model, or a run starting): the pack goes, unless another window still
        // shows this model with its results. The comments stay beside the model.
        if (!now.Locked)
        {
            var gone = !LockedElsewhere(window, pack) && Remove(now.File);
            before = now;
            return gone ? $"{link.Program.Name}: {Path.GetFileName(now.File)} has no results now (unlocked) - its pack was taken away; the reviewer's comments are kept in {Path.GetFileName(CommentsFile(now.File))} and come back with the next run." : null;
        }
        string? why;
        Written written;
        lock (LockFor(pack))
        {
            why = Decide(before, now, MadeOf(pack), nowUtc ?? DateTime.UtcNow);
            if (why is null) { before = now; return null; }
            written = Write(link, now, why);
        }
        before = now;
        return $"{link.Program.Name}: {Path.GetFileName(now.File)} - pack written after the {why} ({written.Frames} frames, {written.Areas} areas, {written.Tables} tables" +
               (written.Comments > 0 ? $", {written.Comments} comments kept" : "") + $", {written.Took.TotalSeconds:0} s): {written.Pack}";
    }
}
