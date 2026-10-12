namespace UvssService.Data.Entities;

/// <summary>ANPR (plate-reading) IP camera for one lane. Same shape as
/// DriverCamera (see its own remarks on why no RTSP/stream URL is built or
/// shown) -- kept as its own table/entity rather than sharing DriverCamera's,
/// since the two are managed as separate resource lists on the Lane Setup
/// page.
///
/// Enabled/Source used to live duplicated in appsettings.Lanes.json; not
/// exposed on the Lane Setup form -- edit them directly in the database, or
/// via a future "Advanced Settings" UI, until/unless that's needed.</summary>
public class AnprCamera
{
    public int Id { get; set; }
    public string LaneId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>"axis" -- the real on-site Axis snapshot camera (the only
    /// supported source; there is no simulated mode).</summary>
    public string Source { get; set; } = "axis";
}
