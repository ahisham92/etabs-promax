using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Safe;

/// <summary>
/// The one story of a blank SAFE model, put at the level of the file built in it. SAFE 22.7 refused every floor of the
/// Modeler's .f2k (slabs at 3.2 m) in a blank model whose story was at 3 m: "SAFE refused to add an area with section
/// S200" (tester's SAFE-only field test, 0.4.66, 2026-11-27). Best effort: the build says why when a floor is still refused.
/// </summary>
public static class SafeStory
{
    /// <summary>Moves the model's top story to <paramref name="level"/> when no story is there, and says where its stories are.</summary>
    public static string AtLevel(object model, double level, double? height = null)
    {
        var said = Level(model, level);
        return height is { } h && h > 0 ? said + Height(model, level, h) : said;
    }

    /// <summary>
    /// The story at the level also takes the file's story height, so the base is where the file's is: SAFE 20.1 kept the
    /// height 1 of its blank story after the story was moved from 1 to 3.2, and refused the Modeler's columns from Z = 0
    /// (tester's SAFE-only field test, 0.4.76, 2026-11-28: "SAFE refused to add a frame with section C500x500").
    /// </summary>
    internal static string Height(object model, double level, double height)
    {
        var at = Read(model).FirstOrDefault(s => Math.Abs(s.Elevation - level) < 1e-4);
        if (at is null || at.Height <= 0 || Math.Abs(at.Height - height) < 1e-4) return "";
        try
        {
            ApiCall.Must(model, "Story.SetHeight", new Dictionary<string, object?> { ["Name"] = at.Name, ["Height"] = height }, $"give the story {at.Name} the height {Number(height)}");
            var now = Read(model);
            // A program that keeps the base and moves the story with its height: the story goes back to the level.
            if (!now.Any(s => Math.Abs(s.Elevation - level) < 1e-4))
                ApiCall.Must(model, "Story.SetElevation", new Dictionary<string, object?> { ["Name"] = at.Name, ["Elevation"] = level }, $"move the story {at.Name} to {Number(level)}");
            var after = Read(model).FirstOrDefault(s => s.Name == at.Name);
            return after is not null && Math.Abs(after.Height - height) < 1e-4 && Math.Abs(after.Elevation - level) < 1e-4
                ? $", height {Number(at.Height)} made the file's {Number(height)}"
                : $", height {Number(at.Height)} asked to be {Number(height)} (it is {Number(after?.Height ?? 0)} at {Number(after?.Elevation ?? 0)})";
        }
        catch (Exception ex) when (ex is EtabsException or ToolInputException or MissingMethodException) { return $", height {Number(at.Height)} not the file's {Number(height)} ({ex.Message})"; }
    }

    private static string Level(object model, double level)
    {
        var stories = Read(model);
        if (stories.Count == 0) return "its stories could not be read";
        if (stories.Any(s => Math.Abs(s.Elevation - level) < 1e-4)) return "its story is at the file's level " + Number(level);
        var top = stories[^1];
        var below = stories.Count > 1 ? stories[^2].Elevation : 0;
        var tried = new List<string>();
        foreach (var (path, named) in new (string, Dictionary<string, object?>)[]
        {
            ("Story.SetElevation", new() { ["Name"] = top.Name, ["Elevation"] = level }),
            ("Story.SetHeight", new() { ["Name"] = top.Name, ["Height"] = level - below }),
        })
        {
            try
            {
                ApiCall.Must(model, path, named, $"move the story {top.Name} to {Number(level)}");
                var now = Read(model);
                if (now.Any(s => Math.Abs(s.Elevation - level) < 1e-4)) return $"its story {top.Name} moved from {Number(top.Elevation)} to the file's level {Number(level)} ({path})";
                tried.Add($"{path} answered 0 but the story stayed at {Number(top.Elevation)}");
            }
            catch (Exception ex) when (ex is EtabsException or ToolInputException or MissingMethodException) { tried.Add($"{path}: {ex.Message}"); }
        }
        return $"its story {top.Name} stays at {Number(top.Elevation)}, not the file's level {Number(level)} ({string.Join("; ", tried)})";
    }

    /// <summary>The model's stories, lowest first, by GetStories_2 or the older GetStories.</summary>
    internal static List<StoryRow> Read(object model)
    {
        foreach (var path in new[] { "Story.GetStories_2", "Story.GetStories" })
        {
            try
            {
                var call = ApiCall.Invoke(model, path);
                if (!ApiCall.Worked(call)) continue;
                return Rows(call);
            }
            catch (Exception ex) when (ex is EtabsException or ToolInputException or MissingMethodException) { }
        }
        return [];
    }

    /// <summary>The stories in a GetStories answer: StoryNames with StoryElevations (or StoryElevation), lowest first.</summary>
    internal static List<StoryRow> Rows(JsonObject call)
    {
        var names = ApiCall.Value(call, "StoryNames") as JsonArray;
        var elevations = (ApiCall.Value(call, "StoryElevations") ?? ApiCall.Value(call, "StoryElevation")) as JsonArray;
        var heights = (ApiCall.Value(call, "StoryHeights") ?? ApiCall.Value(call, "StoryHeight")) as JsonArray;
        if (names is null || elevations is null) return [];
        var rows = new List<StoryRow>();
        for (var i = 0; i < Math.Min(names.Count, elevations.Count); i++)
            rows.Add(new StoryRow(names[i]?.ToString() ?? "", ApiCall.Number(elevations[i]), heights is not null && i < heights.Count ? ApiCall.Number(heights[i]) : 0));
        return rows.OrderBy(r => r.Elevation).ToList();
    }

    private static string Number(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
