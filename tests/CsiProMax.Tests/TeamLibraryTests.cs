using System.Text.Json;
using CsiProMax.Tools;

/// <summary>
/// The team library (Ahmed, 2026-11-25: learned tools "published to github so other users can take advantage", used by
/// three members, synced through the app every hour or two). The sync's rules are checked by the same check the field
/// test runs on every PC (TeamSyncCheck); here also the tool file's new fields and what a tool says in another program.
/// </summary>
static class TeamLibraryTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cpm-team-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static readonly HashSet<string> BuiltIn = ToolRegistry.CreateDefault().All.Select(t => t.Name).ToHashSet();

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("team library: the sync's rules hold (publish, receive, fix, conflicts, programs, bad code, deletes)", () =>
        {
            var root = Temp();
            try
            {
                var said = TeamSyncCheck.Run(root, t => TeamLibrary.Check(t, BuiltIn));
                Check(said.StartsWith("9 rules held", StringComparison.Ordinal), said);
            }
            finally { Directory.Delete(root, true); }
        });

        yield return ("team library: a tool from the library with a built-in tool's name, or code that does not compile, is not taken", () =>
        {
            var fine = LearnedTool.Create("count_things", "x", "return 1;", "{}", [], false, 0);
            Check(TeamLibrary.Check(fine, BuiltIn) is null, "a fine tool was refused: " + TeamLibrary.Check(fine, BuiltIn));
            var builtIn = LearnedTool.Create("save_as_tool", "x", "return 1;", "{}", [], false, 0);
            Check(TeamLibrary.Check(builtIn, BuiltIn)?.Contains("built-in", StringComparison.Ordinal) == true, "a tool named like a built-in one was taken");
            var broken = LearnedTool.Create("broken_thing", "x", "return nope;", "{}", [], false, 0);
            Check(TeamLibrary.Check(broken, BuiltIn)?.Contains("compile", StringComparison.Ordinal) == true, "code that does not compile was taken");
        });

        yield return ("team library: a tool says who made it and where it ran, and the file reads back the same on any PC", () =>
        {
            var tool = LearnedTool.Create("count_team_beams", "Counts beams.", "return 1;", """{"story":{"type":"string"}}""", ["story"], false, 3,
                null, "yousefsedikdar", ["sap2000", "ETABS"]);
            Check(tool.Programs.SequenceEqual(["etabs", "sap2000"]), "programs are kept in one form: " + string.Join(",", tool.Programs));
            var back = LearnedTool.FromJson(tool.ToJson());
            Check(back.Author == "yousefsedikdar" && back.Programs.SequenceEqual(["etabs", "sap2000"]) && back.Uses == 3, "author, programs or uses were lost: " + tool.ToJson());
            Check(back.SharedJson() == tool.SharedJson(), "the same tool gives another text after a round trip, so every sync would publish it again");
            Check(LearnedTool.FromJson(back.ToJson()).SharedJson() == back.SharedJson(), "the text is not stable after two round trips");
            Check(!tool.SharedJson().Contains("\"uses\"", StringComparison.Ordinal), "this PC's use count would travel to the team");
            Check(tool.Description.Contains("by yousefsedikdar", StringComparison.Ordinal), "the description does not say who made it: " + tool.Description);
        });

        yield return ("team library: a tool saved before 0.4.16 (no author, no programs) still loads and says nothing about programs", () =>
        {
            const string old = """
            {"name":"old_tool","description":"[Learned tool] Old.","summary":"Old.","modifiesModel":false,
             "inputSchema":{"type":"object","properties":{},"required":[]},"code":"return 1;","uses":2,"saved":"2026-11-20T10:00:00+02:00"}
            """;
            var tool = LearnedTool.FromJson(old);
            Check(tool.Author == "" && tool.Programs.Length == 0 && tool.Uses == 2, "an old tool did not load as it was");
            Check(CsiProMax.Bridge.ConnectorTools.ProgramNote(tool.Programs, "safe") == "", "an old tool got a note on programs");
        });

        yield return ("team library: in a program a tool has not run in, it is offered with a note; where it ran, without", () =>
        {
            var note = CsiProMax.Bridge.ConnectorTools.ProgramNote(["etabs"], "sap2000");
            Check(note.Contains("Made in ETABS", StringComparison.Ordinal) && note.Contains("not run in SAP2000", StringComparison.Ordinal), "the note does not say where it was made and where not: " + note);
            Check(CsiProMax.Bridge.ConnectorTools.ProgramNote(["etabs", "sap2000"], "sap2000") == "", "a tool that ran here got a note");
            // The chat's own tool list (read from the files) carries the same note.
            var dir = Temp();
            try
            {
                var tool = LearnedTool.Create("count_team_slabs", "Counts slabs.", "return 1;", "{}", [], false, 0, null, "emmoneam", ["safe"]);
                File.WriteAllText(Path.Combine(dir, "count_team_slabs.json"), tool.ToJson());
                var listed = CsiProMax.Bridge.ConnectorTools.Compose([], dir).Single(t => t.Name == "count_team_slabs");
                var here = CsiProMax.Csi.Program.Key == "safe" ? "" : "Made in SAFE";
                Check(listed.Description!.Contains(here, StringComparison.Ordinal) && listed.Description.Contains("by emmoneam", StringComparison.Ordinal),
                    "the chat's list does not say who made it or where: " + listed.Description);
            }
            finally { Directory.Delete(dir, true); }
        });

        yield return ("team library: the library is private and its own, not the public CSI ProMax repository", () =>
        {
            Check(TeamLibrary.DefaultRepo != TeamLibrary.TeamRepo, "the library must not be the public repository");
            Check(TeamSync.Local("cpm_field_test_tool") && !TeamSync.Local("count_frames"), "the field test's tools must stay on the PC, and only those");
            var src = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Tools/TeamLibrary.cs"));
            Check(src.Contains("@private = true", StringComparison.Ordinal), "the library is not created private");
        });
    }
}
