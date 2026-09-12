using System.Collections.Concurrent;

namespace UvssService.Cameras;

/// <summary>Per-lane "which stock vehicle is currently showing" index for
/// SimulatedIpCamera (and, if it's ever pointed at a directory again,
/// SimulatedAreaScanCamera) -- advanced exactly once per pass, at vehicle
/// entry, by LaneWorkerHostedService. NOT a fixed wall-clock timer: a timer
/// can't fully guarantee no mid-pass switch (a long-running pass can
/// straddle a timer boundary and see two different simulated vehicles
/// partway through), which is exactly what caused the driver/plate/vehicle
/// tiles to mismatch. Advancing only at pass boundaries ties "vehicle
/// identity" to the same real-world event a physical camera would
/// naturally track -- one vehicle, for the whole time it's actually
/// there -- so every camera reading this slot during a pass, no matter how
/// long the pass runs, sees the same index throughout.</summary>
public class SimulatedVehicleSlot
{
    private int _index;
    public int Current => Volatile.Read(ref _index);
    public void Advance() => Interlocked.Increment(ref _index);
}

/// <summary>Registry so CameraStreamingHostedService (which owns the
/// driver/ANPR/overview cameras) and LaneWorkerHostedService (which knows
/// when a pass actually starts) can share one slot per lane despite being
/// two separate hosted services.</summary>
public class SimulatedVehicleSlotStore
{
    private readonly ConcurrentDictionary<string, SimulatedVehicleSlot> _slots = new();

    public SimulatedVehicleSlot GetOrCreate(string laneName) =>
        _slots.GetOrAdd(laneName, _ => new SimulatedVehicleSlot());
}
