using CsiProMax.Chat;
using CsiProMax.Settings;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's steps for the chat's defaults, the autopilot and the phone (Ahmed, 26 Nov 2026: "by default the names
/// of the guys as coordinator Pitso and workers Ali, Carmen, Carla and Dieng; by default not by coordinator but simple
/// chat; make sure the autopilot option is covered; even the simple chat can be handled by phone once the phone is on").
/// The same steps run in ETABS, SAP2000 and SAFE (the chat is one for all three). Nothing here changes the PC's settings,
/// sends anything to the phone or starts a run: the rules are checked as the chat uses them, on copies.
/// </summary>
public static class ChatFieldSteps
{
    public static void Run(FieldContext ctx)
    {
        ctx.Section("Chat: defaults, autopilot and phone");

        ctx.Step("the crew's names by default: coordinator Pitso, workers Ali, Carmen, Carla and Dieng", () =>
        {
            var names = CrewNames.Defaults;
            FieldContext.Expect(names.Coordinator == "Pitso", "the coordinator is " + names.Coordinator);
            FieldContext.Expect(string.Join(", ", names.Workers) == "Ali, Carmen, Carla, Dieng", "the workers are " + string.Join(", ", names.Workers));
            FieldContext.Expect(names.Worker(4) == "Worker 5", "a fifth job at once is not Worker 5");
            // A PC that pressed Save in Settings before had the old names stored as if typed: they read as the new ones.
            var old = AgentSettings.Upgraded(new AgentSettings { ChatCrewCoordinator = "Atlas", ChatCrewWorkers = "Nova, Echo, Pixel" });
            var read = CrewNames.From(old.ChatCrewYou, old.ChatCrewCoordinator, old.ChatCrewWorkers, old.ChatCrewApplier, "x");
            FieldContext.Expect(read.Coordinator == "Pitso" && read.Workers.Count == 4 && read.Workers[0] == "Ali", "the old stored defaults (Atlas; Nova, Echo, Pixel) are still shown");
            var mine = AgentSettings.Upgraded(new AgentSettings { ChatCrewCoordinator = "Sam", ChatCrewWorkers = "A, B" });
            FieldContext.Expect(mine.ChatCrewCoordinator == "Sam" && mine.ChatCrewWorkers == "A, B", "names the user typed were replaced");
            var here = AgentSettings.Load();
            var now = CrewNames.From(here.ChatCrewYou, here.ChatCrewCoordinator, here.ChatCrewWorkers, here.ChatCrewApplier, Environment.UserName);
            return $"on this PC: coordinator {now.Coordinator}, workers {string.Join(", ", now.Workers)} (Settings > Chat > Names of the crew)";
        });

        ctx.Step("a new chat is a Simple chat by default; With coordinator when picked", () =>
        {
            FieldContext.Expect(new AgentSettings().ChatTypeDefault == ChatTypes.Simple, "a new install starts With coordinator");
            FieldContext.Expect(ChatTypes.Parse(null) == ChatTypes.Simple && ChatTypes.Parse("") == ChatTypes.Simple && ChatTypes.Parse(ChatTypes.Coordinator) == ChatTypes.Coordinator,
                "a stored type is not read the way the chat stores it");
            FieldContext.Expect(AgentSettings.Upgraded(new AgentSettings { ChatTypeDefault = ChatTypes.Coordinator }).ChatTypeDefault == ChatTypes.Simple,
                "an older settings file (it stored With coordinator, the old default, at every Save) still starts With coordinator");
            FieldContext.Expect(AgentSettings.Upgraded(new AgentSettings { ChatTypeDefault = ChatTypes.Coordinator, ChatTypeChosen = true }).ChatTypeDefault == ChatTypes.Coordinator,
                "With coordinator picked in Settings since is not kept");
            FieldContext.Expect(ChatTypes.All[0] == ChatTypes.Simple, "Settings and New chat do not offer Simple chat first");
            var here = AgentSettings.Load();
            return $"on this PC new chats start as {ChatTypes.Label(ChatTypes.Parse(here.ChatTypeDefault))}" + (here.ChatTypeChosen ? " (picked in Settings)" : " (the default)");
        });

        ctx.Step("autopilot: the run's states, its stop words and its answers for the user", () =>
        {
            var run = new AutopilotRun();
            FieldContext.Expect(run.State == AutopilotRun.Planning && !run.Active, "a new run is not planning, or already counts as running");
            run.State = AutopilotRun.Running;
            FieldContext.Expect(run.Active && Autopilot.RunWatchesAnswer(run), "a running run is not active, or its own stuck-answer watch is off");
            run.State = AutopilotRun.Paused;
            FieldContext.Expect(run.Active && !Autopilot.RunWatchesAnswer(run), "a paused run is not kept, or still watched as running");
            run.State = AutopilotRun.Done;
            FieldContext.Expect(!run.Active, "a finished run still counts as running");
            FieldContext.Expect(Autopilot.SaysStop("stop") && Autopilot.SaysStop("please stop the autopilot") && !Autopilot.SaysStop("stop adding beams on level 3"),
                "a stop typed (or sent from the phone) is not read as a stop, or an instruction is read as one");
            FieldContext.Expect(Autopilot.Allows("deletes 12 frames").Allow, "a change asked while the user is away is not allowed after the plan was approved");
            FieldContext.Expect(Autopilot.SafeResult(4) == 7 && Autopilot.SafeResult(1) == 2 && Autopilot.SafeResult(0) == 1 && Autopilot.SafeResult(-1) == 2,
                "a program dialog nobody can click is not answered with No, Cancel or OK as it should be");
            var t0 = new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);
            FieldContext.Expect(Autopilot.Stalled(t0, t0 + Autopilot.TurnStuck, false, default) && !Autopilot.Stalled(t0, t0 + TimeSpan.FromMinutes(5), true, default),
                "a stuck answer is not found, or a long step in the program is taken for one");
            FieldContext.Expect(Autopilot.PlanRequest("model the tower").Contains(Autopilot.QuestionsHeading, StringComparison.Ordinal),
                "the plan does not end with the questions before the user leaves");
            return "planning, approval, running, paused, done and stopped; stop words; changes and program dialogs answered while away; stuck answers";
        });

        ctx.Step("autopilot: its settings on this PC", () =>
        {
            var s = AgentSettings.Load();
            FieldContext.Expect(s.AutopilotFiveHourMax is > 0 and <= 100 && s.AutopilotWeekShare is >= 0 and <= 100 && s.AutopilotJobBudget is >= 0 and <= 100,
                "a usage limit for the autopilot is out of range");
            FieldContext.Expect(AskRules.WaitChoices.Contains(s.AutopilotAskWaitMinutes) || s.AutopilotAskWaitMinutes > 0, "the wait for a phone answer is not one Settings offers");
            return $"pauses at {s.AutopilotFiveHourMax}% of the 5-hour limit and after {s.AutopilotWeekShare}% of the week; a job pauses at {s.AutopilotJobBudget}%; " +
                   $"phone questions wait {s.AutopilotAskWaitMinutes} min; progress to the phone every {s.PhoneUpdateMinutes} min; " +
                   (s.AutopilotResumeWeekly ? "carries on by itself when the weekly limit resets" : "waits for Resume after a weekly limit");
        });

        ctx.Step("phone: a Simple chat answers the phone in its own voice, and says it is still on a long task", () =>
        {
            var simple = AskRules.PhoneTurn("add a beam on grid A", "Pitso", "", crew: false);
            var crew = AskRules.PhoneTurn("add a beam on grid A", "Pitso", "", crew: true);
            FieldContext.Expect(simple.Contains("from you, Pitso", StringComparison.Ordinal) && simple.Contains("no workers", StringComparison.Ordinal) && !simple.Contains("the workers are on", StringComparison.Ordinal),
                "a Simple chat's answer to the phone talks about workers it does not have");
            FieldContext.Expect(crew.Contains("the workers are on", StringComparison.Ordinal), "a chat With coordinator's answer to the phone does not say what the workers are on");
            FieldContext.Expect(simple.Contains("asked on their phone", StringComparison.Ordinal), "the chat is not told that an OK is asked on the phone");
            var still = AskRules.StillOn(TimeSpan.FromMinutes(12), "Set lateral load");
            FieldContext.Expect(still.StartsWith("Still on it (12 min", StringComparison.Ordinal) && still.Contains("Set lateral load", StringComparison.Ordinal), "the long-task line reads " + still);
            return "the answer goes back to the phone from Pitso; every " + AskRules.PhoneTurnUpdateMinutes + " min of a long task the phone hears it is still on it";
        });

        const string approvals = "phone: a change that needs an OK asks on the phone (Allow / Don't allow), never a dialog nobody sees";
        if (PhoneApprovals.Turn) ctx.Skip(approvals, "a message from the phone is being answered now; run the field test again when it is done");
        else ctx.Step(approvals, () =>
        {
            var asked = new List<UserQuestion>();
            var before = (PhoneApprovals.TestPhoneOn, PhoneApprovals.TestAsk);
            // Calls of their own (a new id each run), so nothing the user asked or allowed on the phone is touched.
            var a = "{\"field test\":\"" + Guid.NewGuid().ToString("N") + "\",\"ids\":[1,2,3]}";
            var b = "{\"field test\":\"" + Guid.NewGuid().ToString("N") + "\",\"ids\":[9]}";
            try
            {
                PhoneApprovals.TestPhoneOn = () => true;
                PhoneApprovals.TestAsk = (chat, text, key, topic) =>
                {
                    var q = new UserQuestion { Id = 900 + asked.Count, JobKey = chat, Question = text, Approval = key, Topic = topic, Asked = DateTime.UtcNow, Options = [PhoneApprovals.Allow, PhoneApprovals.DontAllow] };
                    asked.Add(q);
                    return q;
                };
                FieldContext.Expect(!PhoneApprovals.Active, "approvals go to the phone although no turn from the phone works");
                PhoneApprovals.TurnStarted("chat-field-test", "Field test");
                FieldContext.Expect(PhoneApprovals.Active, "a turn from the phone still asks in a dialog on the PC");
                var now = DateTime.UtcNow;
                var first = PhoneApprovals.Decide("delete_elements", "Delete elements", "it deletes 3 frames", a, now);
                FieldContext.Expect(!first.Allowed && asked.Count == 1 && first.Line!.Contains("Q900", StringComparison.Ordinal), "the change was made without the user's OK, or not asked on the phone");
                FieldContext.Expect(AskRules.PhoneOptions(asked[0]).SequenceEqual([PhoneApprovals.Allow, PhoneApprovals.DontAllow]), "the phone does not show Allow and Don't allow");
                var again = PhoneApprovals.Decide("delete_elements", "Delete elements", "it deletes 3 frames", a, now.AddSeconds(20));
                FieldContext.Expect(!again.Allowed && asked.Count == 1, "asked again while the first ask waits, or made without a tap");
                FieldContext.Expect(!PhoneApprovals.Decide("delete_elements", "Delete elements", "", b, now).Allowed && asked.Count == 2, "another call took the first call's ask");
                asked[0].Answer = PhoneApprovals.Allow;
                asked[0].Answered = now.AddMinutes(1);
                FieldContext.Expect(PhoneApprovals.AnsweredLine(asked[0]).Contains("make the same call again", StringComparison.Ordinal), "the chat is not told to go ahead after Allow");
                FieldContext.Expect(PhoneApprovals.Decide("delete_elements", "Delete elements", "", a, now.AddMinutes(2)).Allowed, "the same call after Allow is still refused");
                FieldContext.Expect(!PhoneApprovals.Decide("delete_elements", "Delete elements", "", a, now.AddMinutes(3)).Allowed, "one Allow allowed the call twice");
                asked[1].Answer = PhoneApprovals.DontAllow;
                asked[1].Answered = now.AddMinutes(1);
                var refused = PhoneApprovals.Decide("delete_elements", "Delete elements", "", b, now.AddMinutes(2));
                FieldContext.Expect(!refused.Allowed && refused.Line!.Contains("was not done", StringComparison.Ordinal), "Don't allow on the phone let the change through");
                FieldContext.Expect(!PhoneApprovals.Allows("Don't allow") && !PhoneApprovals.Allows("no") && PhoneApprovals.Allows("Allow") && PhoneApprovals.Allows("yes go ahead"), "the phone's answers are read the wrong way");
                PhoneApprovals.TurnEnded();
                FieldContext.Expect(!PhoneApprovals.Active, "approvals still go to the phone after the turn from the phone ended");
            }
            finally
            {
                PhoneApprovals.TurnEnded();
                PhoneApprovals.Forget(a);
                PhoneApprovals.Forget(b);
                (PhoneApprovals.TestPhoneOn, PhoneApprovals.TestAsk) = before;
            }
            return "asked once per call, made only after Allow (once), never after Don't allow; at the PC the dialog asks as before. Nothing was sent to the phone.";
        });

        ctx.Step("phone: on this PC", () =>
        {
            var connected = Phone.PhoneLink.Connected;
            return !connected ? "no phone connected (Chat > Phone: connect)"
                : Phone.PhoneLink.On ? "connected and on: Simple chats and chats With coordinator take messages from it and answer there"
                : "connected, switched off (Chat > Phone: on before you leave the PC)";
        });
    }
}
