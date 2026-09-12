namespace UvssService.Data.Entities;

/// <summary>Basler area-scan (under-vehicle) camera for one lane. No IP
/// field, deliberately -- unlike the driver/ANPR IP cameras, the real
/// camera connection (BaslerAreaScanCamera) never takes or uses an IP at
/// all: pylon's GigE Vision discovery finds the camera on the network by
/// DeviceSerial alone. No username/password either -- there is no
/// login-based stream URL to build for this camera type.
///
/// Source/TestImagePath/SimulatedFramesPerSecond/SimulatedFrameHeightPx used
/// to live duplicated in appsettings.Lanes.json; not exposed on the Lane
/// Setup form (they're dev-simulation/calibration knobs, not lane identity)
/// -- edit them directly in the database, or via a future "Advanced
/// Settings" UI, until/unless that's needed. See BaslerCameraOptions'
/// own remarks for the frame-rate/frame-height sizing math and the
/// production-validated 200fps/75px values these default to.</summary>
public class UvssCamera
{
    public int Id { get; set; }
    public string LaneId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public string DeviceSerial { get; set; } = "";

    /// <summary>"pylon" for the real on-site Basler camera; "simulated" for
    /// local dev/testing without hardware.</summary>
    public string Source { get; set; } = "simulated";

    /// <summary>Simulated mode only -- a test video/image path.</summary>
    public string TestImagePath { get; set; } = "";

    public int SimulatedFramesPerSecond { get; set; } = 200;
    public int SimulatedFrameHeightPx { get; set; } = 75;
}
