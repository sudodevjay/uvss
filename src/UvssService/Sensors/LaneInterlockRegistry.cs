using System.Collections.Concurrent;

namespace UvssService.Sensors;

/// <summary>Lets something OTHER than LaneWorkerHostedService (the Lane
/// Setup page's "Test Barrier Relay" button) reach a running lane's actual
/// ILaneInterlock instance by lane name, to manually pulse the barrier relay
/// for on-site wiring verification -- the same registry pattern
/// SimulatedVehicleSlotStore/CameraStreamRegistry already use to share
/// per-lane state across otherwise-unconnected parts of the app.
///
/// Deliberately keyed by lane name/LaneId, not by the Lane Setup Controller
/// row's own database Id -- a Controller row only actually has a live
/// interlock to test once that row's Enabled flag is true and
/// LaneWorkerHostedService has started it (see LaneRuntimeConfigLoader);
/// this database IS what drives which lanes actually run, but a row can
/// still be edited (e.g. disabled, or added seconds ago) without a service
/// restart to pick it up yet.</summary>
public class LaneInterlockRegistry
{
    private readonly ConcurrentDictionary<string, ILaneInterlock> _interlocks = new();

    public void Register(string laneName, ILaneInterlock interlock) => _interlocks[laneName] = interlock;

    public bool TryGet(string laneName, out ILaneInterlock? interlock) => _interlocks.TryGetValue(laneName, out interlock);
}
