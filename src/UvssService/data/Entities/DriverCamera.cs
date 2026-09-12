namespace UvssService.Data.Entities;

/// <summary>Driver-view IP camera for one lane. Password is stored but
/// never displayed anywhere in the Lane Setup UI (only Username is shown,
/// for confirming which account is configured) -- the real connection
/// (AxisIpCamera) is an HTTP VAPIX snapshot fetch with digest auth, not
/// RTSP, so there is no stream URL to build or show here at all.
///
/// Enabled/Source/TestImageDir used to live duplicated in
/// appsettings.Lanes.json; not exposed on the Lane Setup form (Source/
/// TestImageDir are dev-simulation knobs, not lane identity) -- edit them
/// directly in the database, or via a future "Advanced Settings" UI, until/
/// unless that's needed.</summary>
public class DriverCamera
{
    public int Id { get; set; }
    public string LaneId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>"axis" for the real on-site Axis snapshot camera;
    /// "simulated" for local dev/testing without hardware (cycles through
    /// TestImageDir).</summary>
    public string Source { get; set; } = "simulated";

    /// <summary>Simulated mode only -- a test image/video path to cycle
    /// through instead of a real snapshot feed.</summary>
    public string TestImageDir { get; set; } = "";
}
