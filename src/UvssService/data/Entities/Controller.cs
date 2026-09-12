namespace UvssService.Data.Entities;

/// <summary>The lane's interlock/loop controller box -- one row per physical
/// lane. LaneId is the human-assigned identifier tying this controller
/// (and, by the same LaneId, the cameras below) to one lane, since a site
/// can have several.
///
/// Also carries every interlock RUNTIME setting (not just identity) --
/// Enabled/InterlockSource/LoopPositionsMetres/TcpPort/DebounceMs/
/// SimulatedIdleSeconds/SimulatedSegmentSeconds used to live duplicated in
/// appsettings.Lanes.json; that file is gone now, this row is the only
/// source of truth LaneWorkerHostedService reads at startup (see
/// LaneRuntimeConfigLoader). These aren't exposed on the Lane Setup form
/// (they're calibration/simulation knobs an installer wouldn't normally
/// touch, not lane identity) -- edit them directly in the database, or via
/// a future "Advanced Settings" UI, until/unless that's needed.</summary>
public class Controller
{
    public int Id { get; set; }
    public string LaneId { get; set; } = "";
    public string GateName { get; set; } = "";

    /// <summary>Also this lane's TCP interlock target address (see
    /// TcpLaneInterlock) -- this PC connects out to Ip:TcpPort as a
    /// client.</summary>
    public string Ip { get; set; } = "";

    /// <summary>Whether LaneWorkerHostedService/CameraStreamingHostedService
    /// start this lane at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>"tcp" for the real on-site interlock; "simulated" for local
    /// dev/testing.</summary>
    public string InterlockSource { get; set; } = "simulated";

    /// <summary>Comma-separated cumulative distances (metres) of each loop
    /// from the first one, ascending, e.g. "0,3" for this site's real 2-loop
    /// (entry+exit) interlock -- see ILaneInterlock's own remarks for why at
    /// least 2 entries.</summary>
    public string LoopPositionsMetres { get; set; } = "0,3";

    /// <summary>"tcp" interlock only -- the port the loop controller
    /// listens on; this PC connects out to it (see TcpLaneInterlock).</summary>
    public int TcpPort { get; set; } = 6000;

    /// <summary>Real interlock only -- a loop input must read continuously
    /// "on" this long before counting as a genuine trip.</summary>
    public int DebounceMs { get; set; } = 50;

    /// <summary>Simulated interlock only -- seconds idle before a synthetic
    /// entry trip.</summary>
    public double SimulatedIdleSeconds { get; set; } = 20;

    /// <summary>Simulated interlock only -- comma-separated seconds for
    /// each segment between consecutive loops, e.g. "6.0". Must have one
    /// entry per gap between loops in LoopPositionsMetres.</summary>
    public string SimulatedSegmentSeconds { get; set; } = "6.0";
}
