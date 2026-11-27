using System.Collections.Concurrent;
using System.Windows.Threading;

namespace CsiProMax.ETABS;

/// <summary>
/// Runs work for the panel and its tools one item at a time on the window's own thread, with the attached ETABS. It has
/// the shape of Sketch ProMax's dispatcher (which queues work for Revit's main thread), so the code that uses it is
/// unchanged. ETABS itself is reached through its API from here; ETABS's own window stays free meanwhile.
/// </summary>
public sealed class EtabsDispatcher
{
    private readonly ConcurrentQueue<Action> _later = new();
    private Dispatcher? _ui;
    private EtabsApp? _app;
    private DispatcherTimer? _idle;
    private volatile bool _running, _stopped;
    private int _waiting;

    /// <summary>True while queued work (a tool call) is running.</summary>
    public bool IsRunning => _running;

    /// <summary>True while anything is queued or running.</summary>
    public bool HasWork => _running || Volatile.Read(ref _waiting) > 0;

    /// <summary>Call on the window's thread, once.</summary>
    public void Initialize(EtabsApp application)
    {
        _app = application;
        _ui = Dispatcher.CurrentDispatcher;
        _idle = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.ApplicationIdle, (_, _) => OnIdle(), _ui);
        _idle.Start();
    }

    public void Shutdown(EtabsApp application)
    {
        _stopped = true;
        _idle?.Stop();
        while (_later.TryDequeue(out _)) { }
    }

    /// <summary>Short UI work at the next idle moment, one item at a time.</summary>
    public void Later(Action work)
    {
        if (_stopped) return;
        _later.Enqueue(work);
    }

    public Task<T> InvokeAsync<T>(Func<EtabsApp, T> work) => InvokeAsync(work, CancellationToken.None);

    public Task<T> InvokeAsync<T>(Func<EtabsApp, T> work, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopped || _ui is null || _app is null)
        {
            done.SetException(new InvalidOperationException("CSI ProMax is closing."));
            return done.Task;
        }
        Interlocked.Increment(ref _waiting);
        _ui.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Interlocked.Decrement(ref _waiting);
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
            _running = true;
            try { done.TrySetResult(work(_app)); }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { _running = false; }
        });
        return done.Task;
    }

    /// <summary>The same as <see cref="InvokeAsync{T}(Func{EtabsApp, T}, CancellationToken)"/>: ETABS has no event-only calls.</summary>
    public Task<T> InvokeFromEventAsync<T>(Func<EtabsApp, T> work, CancellationToken ct = default) => InvokeAsync(work, ct);

    /// <summary>Raised on the window's thread when nothing is running, about four times a second.</summary>
    internal static event Action<EtabsApp>? Idle;

    private void OnIdle()
    {
        if (_stopped || _running || _app is null) return;
        try { Idle?.Invoke(_app); } catch (Exception) { }
        if (_later.TryDequeue(out var work))
        {
            try { work(); } catch (Exception) { }
        }
    }

    /// <summary>How many idle moments were seen: proof the window's thread is turning over.</summary>
    internal int Heard => Volatile.Read(ref _heard);
    private int _heard;

    /// <summary>What holds ETABS's own window (a dialog, a long operation), seen from outside it.</summary>
    internal ETABSBlock.State Blocker() => ETABSBlock.Read(CsiProMax.App.MainWindow);

    internal Task<ETABSBlock.State> BlockerAsync() => Task.FromResult(Blocker());

    /// <summary>The window's thread always picks work up, so there is never a dialog of ETABS's in the way here.</summary>
    internal Task<string?> WaitForPickupAsync(Task pickedUp, string what, int maxMs, int dialogMs, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
