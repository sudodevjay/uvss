using System.Collections.Concurrent;

namespace UvssService.Streaming;

/// <summary>Live health of one (lane, camera label) IP camera -- updated by
/// CameraStreamingHostedService's continuous capture loop every time it
/// grabs a snapshot (success or failure), independent of whether any
/// browser is actually watching that camera's MJPEG stream right now.</summary>
public class CameraStatus
{
    public DateTime? LastFrameUtc { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Lets Lane Setup show a real online/offline status for the
/// Driver/ANPR (and any future Overview) IP cameras -- the same registry
/// pattern LaneInterlockRegistry/SimulatedVehicleSlotStore/
/// CameraStreamRegistry already use to share per-lane state across
/// otherwise-unconnected parts of the app. Keyed by (laneName, cameraLabel)
/// -- "driver"/"anpr"/"overview", matching CameraStreamingHostedService's
/// own labels.</summary>
public class CameraStatusRegistry
{
    /// <summary>How long since the last successfully-grabbed snapshot
    /// before a "real" (non-simulated) camera counts as offline -- several
    /// multiples of StreamIntervalMs (default 150ms) so one slow poll
    /// doesn't flicker between online/offline. Shared by every caller (Lane
    /// Setup's Status column, Home's "VIDEO STREAM" tile) so they always
    /// agree on the same camera's state.</summary>
    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(string Lane, string Camera), CameraStatus> _statuses = new();

    public CameraStatus GetOrCreate(string laneName, string cameraLabel) =>
        _statuses.GetOrAdd((laneName, cameraLabel), _ => new CameraStatus());

    public bool TryGet(string laneName, string cameraLabel, out CameraStatus? status) =>
        _statuses.TryGetValue((laneName, cameraLabel), out status);

    /// <summary>True only if this camera has actually delivered a frame
    /// recently -- for a real ("axis"/"pylon") camera with nothing plugged
    /// in, no frame is EVER published (see CameraStreamingHostedService),
    /// so this stays false forever rather than just going stale. Callers
    /// that also support a "simulated" mode (which always looks online,
    /// since there's no real camera to go silent) should check that
    /// separately -- this only reflects genuine delivered frames.</summary>
    public bool IsOnline(string laneName, string cameraLabel) =>
        TryGet(laneName, cameraLabel, out var status) && status?.LastFrameUtc != null
        && DateTime.UtcNow - status.LastFrameUtc.Value < OnlineTimeout;
}
