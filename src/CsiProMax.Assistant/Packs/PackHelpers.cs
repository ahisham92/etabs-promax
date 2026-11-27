using System.Collections.Concurrent;
using System.Diagnostics;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Packs;

/// <summary>
/// More hands for a large pack (Ahmed 2026-11-27: "we have the option of opening 3 windows, this takes only 1 licence").
/// The program gives one table at a time in each of its windows, and a large model's results are millions of rows
/// (0.4.68 full test: ETABS took about 15 minutes for one large model's pack, four shell tables of 2.1 million rows at
/// about 2.5 minutes each). After the analysis the model and its results are on disk, so hidden windows of the same
/// program open copies of them (as <see cref="PackCopy"/>) and read result tables beside the window that ran it, each a
/// different table at the same time. At most three windows of the program are open at once, the engineer's own
/// counted. The helpers start in the background, so the pack does not wait for them; a helper that cannot start is
/// left out, and the pack is read by fewer hands. Closed, and their copies removed, when the pack is read.
/// </summary>
public sealed class PackHelpers : IDisposable
{
    /// <summary>How many windows of one program may be open at once on one licence (Ahmed 2026-11-27).</summary>
    public static int MostWindows { get; set; } = 3;

    /// <summary>The extra windows asked for a pack: none turns it off.</summary>
    public static int Wanted { get; set; } = 2;

    /// <summary>
    /// The helpers a model of this size needs (Ahmed 2026-11-27: "if the model is small then one window is enough"):
    /// none for a small model, one for a medium, two for a large, by its objects (a slab or wall gives several times a
    /// frame's rows, one for each joint of its mesh) times its load cases and combinations.
    /// </summary>
    public static int ForSize(int frames, int areas, int casesAndCombos)
    {
        var weight = (long)(frames + 4L * areas) * Math.Max(1, casesAndCombos);
        return weight >= LargeWeight ? 2 : weight >= MediumWeight ? 1 : 0;
    }

    /// <summary>From this size (see <see cref="ForSize"/>) one helper; from <see cref="LargeWeight"/> two.</summary>
    public static long MediumWeight { get; set; } = 12_000;
    public static long LargeWeight { get; set; } = 30_000;

    /// <summary>The windows of the program open on this PC now (the tests put a stand-in here).</summary>
    public static Func<CsiProgram, int> Running { get; set; } = p =>
    {
        try { var all = Process.GetProcessesByName(p.ProcessName); foreach (var x in all) x.Dispose(); return all.Length; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 1; }
    };

    private readonly BlockingCollection<ICsiLink> _ready = new();
    private readonly List<IScratchWindow> _windows = [];
    private readonly List<string> _folders = [];
    private readonly List<Thread> _starting = [];
    private readonly List<string> _said = [];
    private readonly object _gate = new();
    private int _pending;
    private volatile bool _closed;

    /// <summary>What happened to each helper, for the manifest.</summary>
    public IReadOnlyList<string> Said { get { lock (_gate) return _said.ToList(); } }

    /// <summary>The helpers asked for (after the windows already open are counted).</summary>
    public int Asked { get; }

    private PackHelpers(int asked) => Asked = asked;

    /// <summary>
    /// Starts up to <see cref="Wanted"/> hidden windows of <paramref name="program"/> on copies of
    /// <paramref name="modelFile"/> and its results, in the background, within <see cref="MostWindows"/> open at once.
    /// </summary>
    public static PackHelpers Open(CsiProgram program, string modelFile, int? wanted = null)
    {
        var room = Math.Max(0, MostWindows - Running(program));
        var helpers = new PackHelpers(Math.Min(wanted ?? Wanted, room));
        if (helpers.Asked == 0 || !File.Exists(modelFile))
        {
            helpers.Say(helpers.Asked == 0 ? $"no helper window: {MostWindows} windows of {program.Name} are open already" : "no helper window: the model is not saved");
            helpers._ready.CompleteAdding();
            return helpers;
        }
        helpers._pending = helpers.Asked;
        for (var i = 0; i < helpers.Asked; i++)
        {
            var n = i + 1;
            var t = new Thread(() => helpers.StartOne(program, modelFile, n)) { IsBackground = true, Name = $"CSI ProMax pack helper {n}" };
            helpers._starting.Add(t);
            t.Start();
        }
        return helpers;
    }

    private void Say(string what) { lock (_gate) _said.Add(what); }

    private void StartOne(CsiProgram program, string modelFile, int n)
    {
        var watch = Stopwatch.StartNew();
        var folder = Path.Combine(PackCopy.Root, $"{Path.GetFileNameWithoutExtension(modelFile)} helper {n} {DateTime.Now:yyyyMMdd-HHmmss-fff}");
        lock (_gate) _folders.Add(folder);
        IScratchWindow? window = null;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var f in PackCopy.FilesOf(modelFile)) PackCopy.CopyShared(f, Path.Combine(folder, Path.GetFileName(f)));
            var ctx = new FieldContext(program, folder, new OfflineEtabs());
            window = PackCopy.Start(program, ctx, Path.Combine(folder, Path.GetFileName(modelFile)));
            if (window is null) { Say($"helper {n}: {program.Name} could not open the copy"); return; }
            lock (_gate) _windows.Add(window);
            if (window is ScratchProgram sp) sp.Hide();
            bool locked;
            try { locked = window.Link.Info().Locked; } catch (EtabsException) { locked = false; }
            if (!locked) { Say($"helper {n}: the copy opened without its analysis results"); return; }
            if (_closed) return;
            Say($"helper {n}: ready in {watch.Elapsed.TotalSeconds:0} s");
            try { _ready.Add(window.Link); } catch (InvalidOperationException) { }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say($"helper {n}: {ex.Message}");
        }
        finally
        {
            if (Interlocked.Decrement(ref _pending) == 0) try { _ready.CompleteAdding(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>The helpers as each becomes ready; ends when every one is ready or has failed.</summary>
    public IEnumerable<ICsiLink> AsReady() => _ready.GetConsumingEnumerable();

    /// <summary>Closes every helper window and removes the copies. Waits for helpers still starting.</summary>
    public void Dispose()
    {
        _closed = true;
        foreach (var t in _starting) t.Join(TimeSpan.FromMinutes(5));
        List<IScratchWindow> windows;
        lock (_gate) windows = _windows.ToList();
        foreach (var w in windows)
        {
            try { w.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            try { w.PutBackPlugIns(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            try { w.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        foreach (var f in _folders)
            try { if (Directory.Exists(f)) Directory.Delete(f, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
