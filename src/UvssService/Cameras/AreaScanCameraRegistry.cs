using System.Collections.Concurrent;

namespace UvssService.Cameras;

/// <summary>Lets Lane Setup read a running lane's actual IAreaScanCamera
/// (the UVSS/Basler camera) by lane name, to show a real online/offline
/// status -- the same registry pattern LaneInterlockRegistry uses for the
/// interlock. Available reflects whether the Basler camera with this lane's
/// DeviceSerial is currently open and connected (BaslerAreaScanCamera's
/// watchdog keeps it up to date and reconnects it).</summary>
public class AreaScanCameraRegistry
{
    private readonly ConcurrentDictionary<string, IAreaScanCamera> _cameras = new();

    public void Register(string laneName, IAreaScanCamera camera) => _cameras[laneName] = camera;

    public bool TryGet(string laneName, out IAreaScanCamera? camera) => _cameras.TryGetValue(laneName, out camera);
}
