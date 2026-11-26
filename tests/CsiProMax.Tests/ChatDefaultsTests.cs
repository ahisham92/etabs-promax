using CsiProMax;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Chat;
using CsiProMax.Settings;

/// <summary>
/// The chat's defaults, the autopilot and the chat from the phone (Ahmed, 26 Nov 2026: "by default the names of the guys as
/// coordinator Pitso and workers Ali, Carmen, Carla and Dieng; by default not by coordinator but simple chat; make sure the
/// autopilot option is covered; even the simple chat can be handled by phone once the phone is on"). The field test's
/// steps for them (ChatFieldSteps) run here too, on a stand-in, so they pass before they reach a PC.
/// </summary>
static class ChatDefaultsTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Source(params string[] path) => File.ReadAllText(Path.Combine([SourceText.Root(), "src", "CsiProMax.Assistant", .. path]));

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("chat defaults: the crew is Pitso with Ali, Carmen, Carla and Dieng; names typed are kept", () =>
        {
            var d = CrewNames.Defaults;
            Check(d.Coordinator == "Pitso" && string.Join("|", d.Workers) == "Ali|Carmen|Carla|Dieng", "defaults: " + d.Coordinator + " / " + string.Join(", ", d.Workers));
            Check(CrewNames.Stored("Pitso", CrewNames.DefaultCoordinator, CrewNames.OldCoordinator) is null, "the default is stored as if typed");
            Check(CrewNames.Stored(" atlas ", CrewNames.DefaultCoordinator, CrewNames.OldCoordinator) is null, "the old default is kept as if typed");
            Check(CrewNames.Stored("Nova,Echo , Pixel", CrewNames.DefaultWorkers, CrewNames.OldWorkers) is null, "the old workers (spaced another way) are kept as if typed");
            Check(CrewNames.Stored("Ali, Carmen, Carla, Dieng", CrewNames.DefaultWorkers, CrewNames.OldWorkers) is null, "the new workers are stored as if typed");
            Check(CrewNames.Stored("Sam", CrewNames.DefaultCoordinator, CrewNames.OldCoordinator) == "Sam", "a name typed is lost");
            Check(CrewNames.Stored("Nova, Echo", CrewNames.DefaultWorkers, CrewNames.OldWorkers) == "Nova, Echo", "workers typed are lost");
            var settings = Source("Panel", "UI", "SettingsWindow.cs");
            Check(settings.Contains("Chat.CrewNames.Stored(_crewCoordinator.Text", StringComparison.Ordinal), "Settings stores the shown default as if typed again");
        });

        yield return ("chat defaults: a new chat is a Simple chat; an older settings file reads so too unless picked since", () =>
        {
            Check(new AgentSettings().ChatTypeDefault == ChatTypes.Simple, "a new install starts With coordinator");
            Check(ChatTypes.Parse(null) == ChatTypes.Simple && ChatTypes.Parse("junk") == ChatTypes.Simple && ChatTypes.Parse("coordinator") == ChatTypes.Coordinator, "Parse");
            Check(ChatStore.NewType == ChatTypes.Simple, "a chat made before the pane read Settings is With coordinator");
            var old = System.Text.Json.JsonSerializer.Deserialize<AgentSettings>("""{"ChatTypeDefault":"coordinator","ChatCrewCoordinator":"Atlas","ChatCrewWorkers":"Nova, Echo, Pixel","ChatCrewApplier":"Bolt"}""")!;
            var up = AgentSettings.Upgraded(old);
            Check(up.ChatTypeDefault == ChatTypes.Simple && up.ChatCrewCoordinator is null && up.ChatCrewWorkers is null && up.ChatCrewApplier is null, "an older file keeps the old defaults");
            var picked = AgentSettings.Upgraded(System.Text.Json.JsonSerializer.Deserialize<AgentSettings>("""{"ChatTypeDefault":"coordinator","ChatTypeChosen":true}""")!);
            Check(picked.ChatTypeDefault == ChatTypes.Coordinator, "With coordinator picked in Settings is lost");
            Check(Source("Panel", "UI", "SettingsWindow.cs").Contains("_settings.ChatTypeChosen = true;", StringComparison.Ordinal), "Settings never marks the type as picked");
            Check(Source("Panel", "Settings", "AgentSettings.cs").Contains("return Upgraded(JsonSerializer.Deserialize<AgentSettings>", StringComparison.Ordinal), "Load reads an older file as it was written");
            // The autopilot is a chat With coordinator whatever the default.
            Check(Source("Panel", "Chat", "ChatPane.Autopilot.cs").Contains("NewChat(ChatTypes.Coordinator);", StringComparison.Ordinal), "New autopilot chat follows the default type");
            Check(Source("Panel", "Chat", "ChatPane.Types.cs").Contains("(\"New autopilot chat\"", StringComparison.Ordinal), "New chat's menu has no autopilot");
        });

        yield return ("phone: a Simple chat taken from the phone asks its OKs there, and goes ahead only after Allow", () =>
        {
            var asked = new List<UserQuestion>();
            PhoneApprovals.Clear();
            PhoneApprovals.TestPhoneOn = () => true;
            PhoneApprovals.TestAsk = (chat, text, key, topic) =>
            {
                var q = new UserQuestion { Id = asked.Count + 1, JobKey = chat, Question = text, Approval = key, Topic = topic, Options = [PhoneApprovals.Allow, PhoneApprovals.DontAllow] };
                asked.Add(q);
                return q;
            };
            try
            {
                Check(!PhoneApprovals.Active, "approvals go to the phone without a turn from it");
                PhoneApprovals.TurnStarted("chat-1", "Pitso (Tower)");
                PhoneApprovals.TestPhoneOn = () => false;
                Check(!PhoneApprovals.Active, "a switched-off phone still gets the approvals");
                PhoneApprovals.TestPhoneOn = () => true;
                var t = new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);
                var first = PhoneApprovals.Decide("delete_elements", "Delete elements", "3 frames", "{}", t);
                Check(!first.Allowed && asked.Count == 1 && asked[0].JobKey == "chat-1" && asked[0].Topic == "Pitso (Tower)", "not asked in the phone chat of the chat at work");
                Check(asked[0].Question.StartsWith("May I delete elements?", StringComparison.Ordinal), asked[0].Question);
                Check(first.Line!.Contains("Q1", StringComparison.Ordinal) && first.Line.Contains("same call", StringComparison.Ordinal), first.Line);
                Check(!PhoneApprovals.Decide("delete_elements", "Delete elements", "3 frames", "{}", t.AddSeconds(5)).Allowed && asked.Count == 1, "asked twice");
                asked[0].Answer = "yes";
                asked[0].Answered = t.AddMinutes(1);
                Check(PhoneApprovals.Decide("delete_elements", "Delete elements", "3 frames", "{}", t.AddMinutes(2)).Allowed, "refused after yes");
                Check(asked[0].Taken, "the chat is told to call again after the call already went");
                Check(!PhoneApprovals.Decide("delete_elements", "Delete elements", "3 frames", "{}", t.AddMinutes(3)).Allowed && asked.Count == 2, "one Allow allowed two calls");
                asked[1].Answer = "Allow";
                asked[1].Answered = t.AddMinutes(3);
                Check(!PhoneApprovals.Decide("delete_elements", "Delete elements", "3 frames", "{}", t.AddMinutes(3) + PhoneApprovals.AllowFor + TimeSpan.FromMinutes(1)).Allowed, "an old Allow still holds");
                PhoneApprovals.Clear();
                PhoneApprovals.TurnStarted("chat-1", "");
                PhoneApprovals.Decide("run_code", "Run code", "", "x", t);
                asked[^1].Answer = "Don't allow";
                asked[^1].Answered = t;
                var no = PhoneApprovals.Decide("run_code", "Run code", "", "x", t.AddSeconds(30));
                Check(!no.Allowed && no.Line!.Contains("was not done", StringComparison.Ordinal), "Don't allow let it through");
                Check(PhoneApprovals.AnsweredLine(asked[^1]).Contains("Don't make that change", StringComparison.Ordinal), "the chat is told to go ahead after Don't allow");
                Check(AskRules.PhoneOptions(asked[^1]).Count == 2 && AskRules.Keep("ok", asked[^1]) == "ok", "the phone's buttons, or an answer read as an assumption");
                PhoneApprovals.TurnEnded();
                Check(!PhoneApprovals.Active, "still on after the turn");
            }
            finally
            {
                PhoneApprovals.Clear();
                PhoneApprovals.TestPhoneOn = null;
                PhoneApprovals.TestAsk = null;
            }
            // Where it is used: the dialog, the bridge's answer to the chat, the steps only the user can do, the phone turn.
            var approvals = Source("Panel", "Bridge", "Approvals.cs");
            Check(approvals.Contains("Chat.PhoneApprovals.Active && TestAnswer is null", StringComparison.Ordinal), "the dialog is shown although the user is on the phone");
            Check(Source("Panel", "Bridge", "BridgeServer.cs").Contains("Approvals.PhoneLine ??", StringComparison.Ordinal), "the chat is told the user rejected it at the PC");
            Check(Source("Panel", "Tools", "Handoff.cs").Split("Chat.PhoneApprovals.Active").Length == 3, "a step only the user can do waits at the PC while they are on the phone");
            var pane = Source("Panel", "Chat", "ChatPane.Autopilot.cs");
            Check(pane.Contains("PhoneApprovals.TurnStarted(", StringComparison.Ordinal) && pane.Contains("PhoneApprovals.TurnEnded();", StringComparison.Ordinal), "the phone turn does not switch approvals to the phone");
            Check(pane.Contains("crew: IsCoordinator", StringComparison.Ordinal), "a Simple chat answers the phone as if it had workers");
            Check(pane.Contains("PhoneTurnProgress();", StringComparison.Ordinal), "a long Simple chat task says nothing to the phone");
            Check(pane.Contains("q.Approval.Length > 0", StringComparison.Ordinal), "a tap on Allow never reaches the chat");
        });

        yield return ("phone: what a Simple chat says to the phone", () =>
        {
            var simple = AskRules.PhoneTurn("hi", "Pitso", "", crew: false);
            Check(simple.Contains("from you, Pitso", StringComparison.Ordinal) && !simple.Contains("workers are on", StringComparison.Ordinal), simple);
            Check(AskRules.PhoneTurn("hi", "Pitso").Contains("workers are on", StringComparison.Ordinal), "a chat With coordinator lost its workers line");
            Check(AskRules.StillOn(TimeSpan.FromMinutes(60), "") == "Still on it (1 h so far). I'll send the answer here when it's done.", AskRules.StillOn(TimeSpan.FromMinutes(60), ""));
            Check(PhoneApprovals.Allows("Allow") && PhoneApprovals.Allows("OK, go ahead") && !PhoneApprovals.Allows("Don't allow") && !PhoneApprovals.Allows("not now") && !PhoneApprovals.Allows(""), "answers");
        });

        yield return ("field test: the chat's defaults, autopilot and phone steps pass, and every program runs them", () =>
        {
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new OfflineEtabs());
            ChatFieldSteps.Run(ctx);
            var failed = ctx.Steps.Where(s => !s.Ok && !s.Skipped).ToList();
            Check(failed.Count == 0, string.Join(" | ", failed.Select(f => f.Name + ": " + f.Detail)));
            Check(ctx.Steps.Count(s => s.Section == "Chat: defaults, autopilot and phone") >= 7, "steps missing: " + ctx.Steps.Count);
            Check(!PhoneApprovals.Turn && PhoneApprovals.TestAsk is null, "the field test left the phone approvals on");
            Check(Source("FieldTest.cs").Contains("ChatFieldSteps.Run(ctx);", StringComparison.Ordinal), "the field test does not run them");
        });
    }
}
