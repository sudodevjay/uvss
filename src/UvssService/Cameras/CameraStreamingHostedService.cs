using Microsoft.Extensions.Options;
using OpenCvSharp;
using UvssService.Config;
using UvssService.Lanes;
using UvssService.Streaming;

namespace UvssService.Cameras;

/// <summary>Continuously grabs frames from every lane's Driver/ANPR/Overview
/// cameras for the whole lifetime of the service -- NOT gated by whether a
/// vehicle is currently present. This is what makes the dashboard's video
/// tiles genuinely live (an operator watching the wall sees continuous
/// video at all times), separate from LaneWorkerHostedService's pass state
/// machine, which now only *subscribes* to the ANPR feed (via AnprFrameHub)
/// while a pass is Active to feed that pass's PlateTrack -- the camera
/// itself never starts or stops per vehicle.</summary>
public class CameraStreamingHostedService : BackgroundService
{
    private readonly List<LaneOptions> _lanes;
    private readonly CameraDefaultsOptions _cameraDefaults;
    private readonly CameraStreamRegistry _registry;
    private readonly CameraStatusRegistry _statusRegistry;
    private readonly LaneStateStore _stateStore;
    private readonly AnprFrameHub _anprFrameHub;
    private readonly DriverFrameHub _driverFrameHub;
    private readonly SimulatedVehicleSlotStore _vehicleSlotStore;

    public CameraStreamingHostedService(
        LaneConfigStore laneConfigStore,
        IOptions<CameraDefaultsOptions> cameraDefaults,
        CameraStreamRegistry registry,
        CameraStatusRegistry statusRegistry,
        LaneStateStore stateStore,
        AnprFrameHub anprFrameHub,
        DriverFrameHub driverFrameHub,
        SimulatedVehicleSlotStore vehicleSlotStore)
    {
        _lanes = laneConfigStore.Lanes.Where(l => l.Enabled).ToList();
        _cameraDefaults = cameraDefaults.Value;
        _registry = registry;
        _statusRegistry = statusRegistry;
        _stateStore = stateStore;
        _anprFrameHub = anprFrameHub;
        _driverFrameHub = driverFrameHub;
        _vehicleSlotStore = vehicleSlotStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        foreach (var lane in _lanes)
        {
            var state = _stateStore.GetOrCreate(lane.Name);
            var vehicleSlot = _vehicleSlotStore.GetOrCreate(lane.Name);
            tasks.Add(StreamCameraAsync(lane.Name, "driver", IpCameraFactory.Create(lane.DriverCamera, _cameraDefaults, vehicleSlot),
                state.SetDriverFrame, frame => _driverFrameHub.Publish(lane.Name, frame), stoppingToken));
            tasks.Add(StreamCameraAsync(lane.Name, "anpr", IpCameraFactory.Create(lane.AnprCamera, _cameraDefaults, vehicleSlot),
                state.SetAnprFrame, frame => _anprFrameHub.Publish(lane.Name, frame), stoppingToken));
            tasks.Add(StreamCameraAsync(lane.Name, "overview", IpCameraFactory.Create(lane.OverviewCamera, _cameraDefaults, vehicleSlot),
                state.SetOverviewFrame, null, stoppingToken));
        }
        await Task.WhenAll(tasks);
    }

    private async Task StreamCameraAsync(
        string laneName, string cameraLabel, IIpCamera camera, Action<byte[]?> onFrame, Action<Mat>? onRawFrame, CancellationToken ct)
    {
        var broadcaster = _registry.GetOrCreate(laneName, cameraLabel);
        var status = _statusRegistry.GetOrCreate(laneName, cameraLabel);
        var interval = TimeSpan.FromMilliseconds(_cameraDefaults.StreamIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var frame = await camera.GetSnapshotAsync(ct);
                if (frame != null)
                {
                    using (frame)
                    {
                        Cv2.ImEncode(".jpg", frame, out var jpeg);
                        onFrame(jpeg);
                        broadcaster.Publish(jpeg);
                        onRawFrame?.Invoke(frame);
                        status.LastFrameUtc = DateTime.UtcNow;
                        status.LastError = null;
                    }
                }
            }
            catch (CameraUnavailableException ex)
            {
                // Logged noisily enough elsewhere in this codebase already;
                // a continuous stream retrying silently on transient
                // failures is the right default here -- still recorded in
                // CameraStatusRegistry so Lane Setup's Status column can
                // show it as offline.
                status.LastErrorUtc = DateTime.UtcNow;
                status.LastError = ex.Message;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{laneName}] {cameraLabel} stream error: {ex.Message}");
                status.LastErrorUtc = DateTime.UtcNow;
                status.LastError = ex.Message;
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
