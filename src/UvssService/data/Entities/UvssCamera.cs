namespace UvssService.Data.Entities;

/// <summary>Basler area-scan (under-vehicle) camera for one lane. No IP
/// field, deliberately -- unlike the driver/ANPR IP cameras, the real
/// camera connection (BaslerAreaScanCamera) never takes or uses an IP at
/// all: pylon's GigE Vision discovery finds the camera on the network by
/// DeviceSerial alone. No username/password either -- there is no
/// login-based stream URL to build for this camera type.
///
/// Source used to live duplicated in appsettings.Lanes.json; not exposed on
/// the Lane Setup form -- edit it directly in the database. See
/// BaslerCameraOptions' own remarks for the frame-rate/frame-height sizing
/// math the real camera must be configured to.</summary>
public class UvssCamera
{
    public int Id { get; set; }
    public string LaneId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public string DeviceSerial { get; set; } = "";

    /// <summary>"pylon" -- the real on-site Basler camera (the only
    /// supported source; there is no simulated mode).</summary>
    public string Source { get; set; } = "pylon";
}
