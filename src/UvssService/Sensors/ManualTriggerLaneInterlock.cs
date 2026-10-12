namespace UvssService.Sensors;

/// <summary>Wraps the real interlock so the dashboard's ENTRY/EXIT buttons
/// can stand in for the entry/exit loops -- for bench testing the real
/// camera + scan pipeline before the loop controller is installed, and for
/// on-site checks afterwards. Nothing here is simulated: a button press is
/// just one more way to trip a loop, and everything downstream (real Basler
/// grabs, live stitch, finalize, DB save) is the normal pass. The real
/// controller keeps working in parallel -- whichever trips first wins.
///
/// Speed is still computed from the configured loop spacing, so with
/// buttons it's (loop distance / time between ENTRY and EXIT presses) --
/// fine for checking the pipeline, not a real vehicle speed.</summary>
public class ManualTriggerLaneInterlock : ILaneInterlock
{
    private readonly ILaneInterlock _inner;
    private readonly object _lock = new();
    private TaskCompletionSource<DateTime>? _entryWaiter;
    private bool _passActive;
    private DateTime? _manualExitTime;

    public ManualTriggerLaneInterlock(ILaneInterlock inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<double> LoopPositionsMetres => _inner.LoopPositionsMetres;

    public bool IsControllerOnline => _inner.IsControllerOnline;

    /// <summary>Starts a pass. Only accepted while the lane is idle and
    /// waiting for a vehicle; returns false otherwise.</summary>
    public bool PressEntry()
    {
        lock (_lock)
        {
            if (_entryWaiter == null)
            {
                return false;
            }
            _entryWaiter.TrySetResult(DateTime.UtcNow);
            _entryWaiter = null;
            return true;
        }
    }

    /// <summary>Ends the current pass. Only accepted while a pass is in
    /// progress; returns false otherwise.</summary>
    public bool PressExit()
    {
        lock (_lock)
        {
            if (!_passActive || _manualExitTime != null)
            {
                return false;
            }
            _manualExitTime = DateTime.UtcNow;
            return true;
        }
    }

    public async Task<DateTime> WaitForEntryAsync(CancellationToken ct)
    {
        var manualEntry = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _passActive = false;
            _manualExitTime = null;
            _entryWaiter = manualEntry;
        }

        using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var realEntry = _inner.WaitForEntryAsync(innerCts.Token);
        Task<DateTime> first;
        using (ct.Register(() => manualEntry.TrySetCanceled(ct)))
        {
            first = await Task.WhenAny(realEntry, manualEntry.Task);
        }

        lock (_lock)
        {
            _entryWaiter = null;
            _passActive = !ct.IsCancellationRequested;
        }
        // Stop the losing real-loop wait; its cancellation is expected.
        innerCts.Cancel();
        _ = realEntry.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return await first;
    }

    public async Task<DateTime?> WaitForLoopAsync(int loopIndex, DateTime previousTripTime, TimeSpan pollTimeout, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_manualExitTime is { } exitTime)
            {
                return exitTime;
            }
        }
        return await _inner.WaitForLoopAsync(loopIndex, previousTripTime, pollTimeout, ct);
    }

    /// <summary>A button press is momentary -- the exit "loop" is never held
    /// engaged, so the pass ends as soon as EXIT is pressed.</summary>
    public Task<bool> IsLoopEngagedAsync(int loopIndex, CancellationToken ct = default) =>
        _inner.IsLoopEngagedAsync(loopIndex, ct);

    public Task OpenBarrierAsync(CancellationToken ct = default) => _inner.OpenBarrierAsync(ct);
}
