namespace UvssService.Sensors;

/// <summary>Fires synthetic loop trips on a timer and just logs barrier
/// state, for local dev/testing without any interlock hardware wired up. Supports
/// per-segment timing so a simulated pass can mimic a vehicle that speeds
/// up or slows down partway through (segmentSeconds.Length must equal
/// loopPositionsMetres.Count - 1).</summary>
public class SimulatedLaneInterlock : ILaneInterlock
{
    private readonly string _laneName;
    private readonly double _idleSeconds;
    private readonly IReadOnlyList<double> _segmentSeconds;

    // No real hardware to read here, so "engaged" is simulated as a brief
    // pulse right after each loop's trip -- long enough for a dashboard
    // indicator to visibly light up, short enough to still look like a
    // vehicle passing rather than staying stuck "on".
    private static readonly TimeSpan SimulatedEngagedPulse = TimeSpan.FromMilliseconds(400);
    private readonly object _tripLock = new();
    private readonly DateTime?[] _lastTripUtc;

    public SimulatedLaneInterlock(string laneName, IReadOnlyList<double> loopPositionsMetres,
        double idleSeconds, IReadOnlyList<double> segmentSeconds)
    {
        if (loopPositionsMetres.Count < 2)
        {
            throw new ArgumentException("Need at least 2 loops (entry + exit).", nameof(loopPositionsMetres));
        }
        if (segmentSeconds.Count != loopPositionsMetres.Count - 1)
        {
            throw new ArgumentException(
                $"segmentSeconds must have one entry per gap between loops "
                + $"({loopPositionsMetres.Count - 1} expected, got {segmentSeconds.Count}).",
                nameof(segmentSeconds));
        }
        _laneName = laneName;
        LoopPositionsMetres = loopPositionsMetres;
        _idleSeconds = idleSeconds;
        _segmentSeconds = segmentSeconds;
        _lastTripUtc = new DateTime?[loopPositionsMetres.Count];
    }

    public IReadOnlyList<double> LoopPositionsMetres { get; }

    public async Task<DateTime> WaitForEntryAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(_idleSeconds), ct);
        var now = DateTime.UtcNow;
        lock (_tripLock)
        {
            _lastTripUtc[0] = now;
        }
        return now;
    }

    public async Task<DateTime?> WaitForLoopAsync(int loopIndex, DateTime previousTripTime, TimeSpan pollTimeout, CancellationToken ct)
    {
        var segmentSeconds = _segmentSeconds[loopIndex - 1];
        var target = previousTripTime.AddSeconds(segmentSeconds);
        var remaining = target - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining < pollTimeout ? remaining : (pollTimeout <= TimeSpan.Zero ? TimeSpan.Zero : pollTimeout), ct);
        }
        if (DateTime.UtcNow < target)
        {
            return null;
        }
        var now = DateTime.UtcNow;
        lock (_tripLock)
        {
            _lastTripUtc[loopIndex] = now;
        }
        return now;
    }

    public Task<bool> IsLoopEngagedAsync(int loopIndex, CancellationToken ct = default)
    {
        lock (_tripLock)
        {
            var lastTrip = loopIndex < _lastTripUtc.Length ? _lastTripUtc[loopIndex] : null;
            var engaged = lastTrip.HasValue && DateTime.UtcNow - lastTrip.Value < SimulatedEngagedPulse;
            return Task.FromResult(engaged);
        }
    }

    public Task OpenBarrierAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[{_laneName}] (simulated) BARRIER -> OPEN");
        return Task.CompletedTask;
    }

    // No real controller here to go silent on us -- simulated is always
    // "online" for dashboard purposes.
    public bool IsControllerOnline => true;
}
