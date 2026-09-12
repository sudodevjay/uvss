using UvssService.Lanes;

namespace UvssService.Data.Entities;

/// <summary>One completed UVSS pass, permanently recorded in MySQL -- the
/// durable replacement for the old in-memory-only EventLogStore (capped at
/// 500 entries, wiped on every restart). Images themselves are NOT stored
/// here as BLOBs -- they're already written to disk by ImageStorage (see
/// LaneWorkerHostedService.FinalizePass), so this just records the path
/// each one actually landed at, which is enough to serve them again later
/// or point a reporting tool at them. A null path means that particular
/// image either didn't exist for this pass (e.g. no foreign-object
/// detection -> no highlighted image) or storage was disabled.</summary>
public class ScanEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string LaneName { get; set; } = "";
    public string PlateText { get; set; } = "";
    public string PlateStateName { get; set; } = "";
    public double SpeedMetresPerSecond { get; set; }
    public bool PlateTextValid { get; set; }
    public bool ForeignObjectDetected { get; set; }
    public string DriverName { get; set; } = "";
    public AdmissionStatus Admission { get; set; }
    public string DossierNotes { get; set; } = "";

    public string? UnderVehicleImagePath { get; set; }
    public string? HighlightedUnderVehicleImagePath { get; set; }
    public string? DriverImagePath { get; set; }
    public string? AnprImagePath { get; set; }
    public string? OverviewImagePath { get; set; }
    public string? PlateCropImagePath { get; set; }
}
