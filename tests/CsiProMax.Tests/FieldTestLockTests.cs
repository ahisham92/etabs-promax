using CsiProMax.Assistant;

/// <summary>
/// One full field test on the PC at a time. Two at once (seen on the field-test PC on 2026-11-25: one from a window,
/// one from the command line, 11 seconds apart) were handed the same SAP2000 window by its API, each opened its own
/// model in it, and one then waited 15 minutes for nothing.
/// </summary>
static class FieldTestLockTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("field test lock: a second full test is refused while one holds the PC, and may start after", () =>
        {
            using var taken = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            var other = new Thread(() =>
            {
                var pc = FieldTest.TakePcLock();
                taken.Set();
                done.Wait(TimeSpan.FromSeconds(20));
                pc.ReleaseMutex();
                pc.Dispose();
            }) { IsBackground = true };
            other.Start();
            Check(taken.Wait(TimeSpan.FromSeconds(20)), "the first test did not get the lock");
            string? refused = null;
            try { FieldTest.TakePcLock().Dispose(); }
            catch (InvalidOperationException ex) { refused = ex.Message; }
            Check(refused is not null && refused.Contains("already running", StringComparison.Ordinal) && refused.Contains("did not start", StringComparison.Ordinal),
                "the second test was not refused: " + (refused ?? "it got the lock"));
            done.Set();
            Check(other.Join(TimeSpan.FromSeconds(20)), "the first test did not let go");
            var again = FieldTest.TakePcLock();
            again.ReleaseMutex();
            again.Dispose();
        });

        yield return ("field test lock: the full test takes it, the window refuses at once, the command line says so and stops", () =>
        {
            var root = SourceText.Root();
            var test = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "FieldTest.cs"));
            Check(test.Contains("if (o.Full) pc = TakePcLock();"), "FieldTest.Run does not take the PC's lock for a full test");
            var start = test.IndexOf("public static Running Start(", StringComparison.Ordinal);
            Check(start > 0 && test.IndexOf("TakePcLock()", start, StringComparison.Ordinal) is var at && at > start && at < test.IndexOf("var run = new Running", start, StringComparison.Ordinal),
                "Start does not refuse before it starts");
            var program = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Program.cs"));
            Check(program.Contains("Another full test holds the PC") && program.Contains("return 2;"), "the command line does not stop with a message");
        });
    }
}
