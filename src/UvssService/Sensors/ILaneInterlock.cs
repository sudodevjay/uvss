namespace UvssService.Sensors;

/// <summary>The interlock hardware for one lane: inductive loops spanning
/// the full lane width (so any vehicle triggers them regardless of wheel
/// track -- unlike a wheel that has to line up with a specific tyre) plus
/// the barrier relay, all on the same interlock box/PLC. The light relay is
/// wired to the same 2 loop inputs but is driven entirely by the
/// controller's own firmware -- this software never commands it.
///
/// This site's real interlock has exactly 2 loops (entry + exit), which is
/// what the lane's Controller row configures (see LaneRuntimeConfigLoader)
/// -- one segment, one average speed for the whole pass. The interface
/// itself is written for N loops (loop 0
/// gates entry, the last one gates exit/LED-off, and any loops in between
/// re-measure speed per segment) purely so it keeps working unchanged if
/// this site ever adds more loops later; it isn't assuming more hardware
/// than is actually installed. Either way, this is deliberately NOT a
/// physical wheel touching a tyre (see LoopPositionsMetres remarks) --
/// that's an industrial conveyor/web technique, not something road-vehicle
/// UVSS uses, precisely because tyre position varies per vehicle and a
/// loop's field doesn't need one.</summary>
public interface ILaneInterlock
{
    /// <summary>Cumulative distance (metres) of each loop from Loop 0,
    /// ascending, e.g. [0, 1.0, 2.0, 3.0] for 4 loops spanning 3m. At least
    /// 2 entries (a bare entry/exit pair still works, just with only one
    /// speed segment for the whole pass).</summary>
    IReadOnlyList<double> LoopPositionsMetres { get; }

    /// <summary>Blocks until Loop 0 (entry) trips. Returns the trip time.</summary>
    Task<DateTime> WaitForEntryAsync(CancellationToken ct);

    /// <summary>Polls for the loop at `loopIndex` (1..LoopPositionsMetres.Count-1)
    /// tripping, relative to `previousTripTime` (the previous loop's trip
    /// time). Blocks for at most `pollTimeout` (0 = check immediately,
    /// don't block) -- callers that also need to do other work between
    /// polls (grabbing line-scan lines) call this repeatedly with a short
    /// timeout rather than one long blocking call. Returns the trip time
    /// once it has actually happened, else null (including on timeout, in
    /// which case the caller should poll again).</summary>
    Task<DateTime?> WaitForLoopAsync(int loopIndex, DateTime previousTripTime, TimeSpan pollTimeout, CancellationToken ct);

    /// <summary>Non-blocking snapshot of whether the loop at `loopIndex` is
    /// physically engaged (something is over its field) RIGHT NOW -- unlike
    /// WaitForEntryAsync/WaitForLoopAsync, this doesn't wait for or consume a
    /// trip event, and it's meant to be polled continuously (idle or not) so
    /// a dashboard can show live entry/exit loop status independent of
    /// whether a pass is in progress.</summary>
    Task<bool> IsLoopEngagedAsync(int loopIndex, CancellationToken ct = default);

    /// <summary>Commands the barrier/gate-arm relay to OPEN. Doing this
    /// automatically only for a specific outcome (an Approved admission
    /// decision) is a business-logic decision made by the caller
    /// (LaneWorkerHostedService, after plate lookup) or by an operator/
    /// installer's manual control, not something this interlock abstraction
    /// itself should assume. There is deliberately no corresponding "close"
    /// call -- the physical barrier relay is configured to auto-close
    /// itself a fixed few seconds after opening, so this side only ever
    /// sends OPEN.</summary>
    Task OpenBarrierAsync(CancellationToken ct = default);

    /// <summary>True if the physical controller has been heard from
    /// recently -- for the real TCP interlock, this tracks a heartbeat the
    /// controller's own firmware sends every few seconds, independent of
    /// loop/pass state. Lets a dashboard show a live online/offline
    /// indicator for the controller itself, separate from whether a vehicle
    /// happens to be mid-pass right now.</summary>
    bool IsControllerOnline { get; }
}
