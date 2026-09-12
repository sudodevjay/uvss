using System.Collections.Concurrent;

namespace UvssService.Cameras;

/// <summary>Lets Lane Setup read a running lane's actual IAreaScanCamera
/// (the UVSS/Basler camera) by lane name, to show a real online/offline
/// status -- the same registry pattern LaneInterlockRegistry uses for the
/// interlock. Unlike the interlock, there's no continuous heartbeat here:
/// Available reflects whether pylon's GigE discovery actually found a real
/// device by DeviceSerial at startup ("pylon" mode) -- always false for the
/// null fallback camera, and not meaningful for "simulated" mode (Lane
/// Setup shows a separate "Simulated" badge in that case instead of reading
/// this at all).</summary>
public class AreaScanCameraRegistry
{
    private readonly ConcurrentDictionary<string, IAreaScanCamera> _cameras = new();

    public void Register(string laneName, IAreaScanCamera camera) => _cameras[laneName] = camera;

    public bool TryGet(string laneName, out IAreaScanCamera? camera) => _cameras.TryGetValue(laneName, out camera);
}
