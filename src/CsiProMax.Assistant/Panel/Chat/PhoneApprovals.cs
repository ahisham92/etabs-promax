using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CsiProMax.Chat;

/// <summary>
/// A chat driven from the phone, Simple chat or With coordinator (Ahmed, 26 Nov 2026: "even the simple chat can be handled
/// by phone once the phone is on"). While a turn answers a message written on the phone, nobody is at the PC to click the
/// approval dialog, so a change that would ask there asks on the phone instead: Allow or Don't allow as buttons. The
/// call is not made meanwhile (the dialog runs on the program's own thread, which must never wait on the network); the
/// chat is told it was sent, and once the user taps Allow the chat is told to make the same call again, which then goes
/// ahead without a dialog. Only a tap or answer on the phone allows it: nothing the chat writes can. An allowance is for
/// that one call (the same tool with the same inputs) and lasts <see cref="AllowFor"/>.
/// No WPF, network or program here (the unit tests and the field test run it): the questions go out through AskUser.
/// </summary>
internal static class PhoneApprovals
{
    public const string Allow = "Allow", DontAllow = "Don't allow";

    /// <summary>An Allow tapped on the phone holds this long for the call to be made again.</summary>
    public static readonly TimeSpan AllowFor = TimeSpan.FromMinutes(30);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, UserQuestion> Asked = new(StringComparer.Ordinal);
    private static volatile bool _turn;
    private static string _chatKey = "", _topic = "";

    /// <summary>Test hook: stands in for "the phone is connected and on" (null: AskUser.ByPhone).</summary>
    internal static Func<bool>? TestPhoneOn;

    /// <summary>Test hook: where a question goes instead of AskUser (null: AskUser.Ask).</summary>
    internal static Func<string, string, string, string, UserQuestion>? TestAsk;

    /// <summary>
    /// A turn answering a message from the phone is working, in the chat <paramref name="chatKey"/> (JobBoard.ChatKey), whose
    /// phone chat is <paramref name="topic"/>. Set when it starts, cleared when it ends.
    /// </summary>
    public static int TurnStarted(string chatKey, string topic)
    {
        lock (Gate)
        {
            _chatKey = chatKey ?? "";
            _topic = topic ?? "";
            _since = DateTime.UtcNow;
            _turn = true;
            return ++_turnId;
        }
    }

    /// <summary>The turn ended (any turn when <paramref name="id"/> is 0, else only that one: a later turn from the phone stays on).</summary>
    public static void TurnEnded(int id = 0)
    {
        lock (Gate)
            if (id == 0 || id == _turnId) _turn = false;
    }

    private static int _turnId;
    private static DateTime _since;

    /// <summary>A turn from the phone is never taken as still working after this long (it ended in a way nothing saw).</summary>
    public static readonly TimeSpan TurnLongest = TimeSpan.FromHours(2);

    /// <summary>A turn from the phone works now and the phone is on: approvals go to the phone, not to a dialog nobody sees.</summary>
    public static bool Active => Turn && (TestPhoneOn?.Invoke() ?? AskUser.ByPhone);

    /// <summary>One call: the tool and what the dialog would show about its inputs.</summary>
    public static string Key(string tool, string details)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(details ?? ""));
        return tool + ":" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>
    /// What the approval dialog does instead while <see cref="Active"/> (Bridge.Approvals.Show): allowed when an Allow tapped
    /// on the phone stands for this call (used up), else not allowed, and the line the chat is told (asked on the phone, or
    /// refused there).
    /// </summary>
    public static (bool Allowed, string? Line) Decide(string tool, string friendly, string reason, string details, DateTime nowUtc)
    {
        var key = Key(tool, details);
        return TryTake(key, nowUtc) ? (true, null) : (false, Ask(key, friendly, reason, nowUtc));
    }

    /// <summary>"Allow", "yes", "ok", "go ahead": allowed. Anything else (Don't allow, no, wait…) is not.</summary>
    public static bool Allows(string? answer)
    {
        var t = (answer ?? "").Trim();
        if (t.Length == 0 || Regex.IsMatch(t, @"\b(don'?t|do not|not|no|never|stop|cancel|wait)\b", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(t, @"^(allow(ed)?|yes|yep|ok(ay)?|go( ahead)?|approved?|do it|sure|fine|proceed)\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The call was allowed on the phone (an Allow tapped within <see cref="AllowFor"/>): true once, and the allowance is used
    /// up. False while it waits for an answer, or after Don't allow.
    /// </summary>
    public static bool TryTake(string key, DateTime nowUtc)
    {
        lock (Gate)
        {
            if (!Asked.TryGetValue(key, out var q) || q.Answer is not { } answer) return false;
            if (!Allows(answer) || nowUtc - q.Answered > AllowFor) return false;
            Asked.Remove(key);
            q.Taken = true; // the chat isn't also told to try again
            return true;
        }
    }

    /// <summary>
    /// The call asks the user: on the phone, once (asked again while it waits, the same question stands). What the chat is
    /// told instead of the call's result.
    /// </summary>
    public static string Ask(string key, string friendly, string reason, DateTime nowUtc)
    {
        UserQuestion q;
        lock (Gate)
        {
            if (Asked.TryGetValue(key, out var old) && (old.Answer is null || Allows(old.Answer) && nowUtc - old.Answered <= AllowFor) && nowUtc - old.Asked < TimeSpan.FromDays(1))
                return Waiting(old, friendly);
            if (old is { Answer: { } no } && !Allows(no))
            {
                Asked.Remove(key);
                return $"The user tapped \"{no}\" on their phone for {AskRules.Label(old)}, so \"{friendly}\" was not done. Don't call it again unless they ask for it.";
            }
            var text = $"May I {Lower(friendly)}? {reason}".Trim();
            q = TestAsk?.Invoke(_chatKey, text, key, _topic) ?? AskUser.AskApproval(_chatKey, text, key, _topic);
            Asked[key] = q;
        }
        return Waiting(q, friendly);
    }

    private static string Waiting(UserQuestion q, string friendly) =>
        $"Nobody is at the PC to approve this, so \"{friendly}\" was not done yet: it was sent to the user's phone as {AskRules.Label(q)} " +
        $"with {Allow} and {DontAllow}. Tell them in your reply that it waits for their tap. When they tap {Allow} you are told, and the same " +
        "call with the same inputs then goes ahead without asking. Don't make the change another way meanwhile.";

    /// <summary>
    /// The user answered an approval question on the phone (AskUser.Answered, for <paramref name="q"/> with an Approval key):
    /// what the chat is told, as the user's own message from the phone.
    /// </summary>
    public static string AnsweredLine(UserQuestion q) => Allows(q.Answer)
        ? $"[From my phone: I tapped {Allow} for {AskRules.Label(q)}.] Go ahead now: make the same call again with the same inputs, then carry on with what I asked."
        : $"[From my phone: I answered \"{q.Answer}\" for {AskRules.Label(q)}.] Don't make that change. Tell me what you'll do instead.";

    /// <summary>A turn from the phone works now (whether or not the phone is on).</summary>
    public static bool Turn => _turn && DateTime.UtcNow - _since < TurnLongest;

    /// <summary>Forgets the asks of calls with these <paramref name="details"/>, whatever their tool (the field test's own).</summary>
    public static void Forget(string details)
    {
        var tail = Key("", details);
        lock (Gate)
            foreach (var key in Asked.Keys.Where(k => k.EndsWith(tail, StringComparison.Ordinal)).ToList()) Asked.Remove(key);
    }

    /// <summary>Forgets every ask (the unit tests).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Asked.Clear();
            _turn = false;
        }
    }

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
}
