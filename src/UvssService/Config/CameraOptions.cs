namespace UvssService.Config;

/// <summary>Driver/ANPR IP camera config for one lane (Axis-style snapshot endpoint).</summary>
public class IpCameraOptions
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Enabled { get; set; } = false;

    /// <summary>"axis" for the real on-site Axis snapshot camera; "simulated"
    /// for local dev/testing without hardware (cycles through TestImageDir).</summary>
    public string Source { get; set; } = "axis";
    public string TestImageDir { get; set; } = "";
}

/// <summary>Basler area-scan camera config for one lane.
///
/// IMPORTANT -- HARDWARE SIZING CHECK BEFORE GO-LIVE: unlike a line-scan
/// sensor (thousands of lines/sec -- always far faster than any real
/// vehicle needs, so full ground coverage is a non-issue), an area-scan
/// camera's frame rate is 1-2 orders of magnitude slower. If the vehicle
/// moves more than one frame's real-world captured length between two
/// consecutive frame grabs, that strip of ground is never captured at all
/// -- no software (CorrelationResampler included) can invent that back
/// afterwards. Before trusting this lane at real traffic speeds, confirm:
///
///   AcquisitionFrameRate (fps) x FrameRealWorldLengthMetres  >=  MaxVehicleSpeedMetresPerSecond
///
/// where FrameRealWorldLengthMetres is however much chassis length one
/// frame actually captures (determined by mounting height + lens FOV +
/// sensor height -- a physical/lens fact, not a pylon setting), read off
/// the real camera's datasheet/configuration, WITH a comfortable margin
/// (at least 2x), not just barely satisfying the inequality -- see
/// AreaScanSpeedSweepTest's own remarks for why margin matters even once
/// coverage is technically gap-free (frame-to-frame overlap needs to stay
/// comfortably above CorrelationResampler's search template size for
/// reliable de-duplication, which shrinks as speed approaches the gap-free
/// ceiling).
///
/// TARGET (validated 2026-09-11 against a real photo, not synthetic
/// texture -- see AreaScanSpeedSweepTest.RunRealImage,
/// `dotnet run -- --test-area-scan-real &lt;imagePath&gt; 200 75`): 200fps
/// with a 75px-tall frame (= ~15cm of chassis length per frame at this
/// system's 500 rows/metre calibration) keeps corrected reconstruction
/// error under 4.3% for every constant speed up to 30 km/h and every
/// requested compound scenario (slow-brake-fast, fast-brake-slow, ramp,
/// oscillating, full-stop-mid-pass) -- zero gaps, with a 108 km/h hard
/// ceiling (3.6x margin over 30 km/h). Note this residual few-percent error
/// is NOT a tuning knob -- chunkHeight/templateHeight/minConfidence made no
/// measurable difference to it in the real-image sweep; it's frame-boundary
/// rounding (how far the last partial frame in a pass over/undershoots the
/// true stop point), which shrinks with a SMALLER frameHeightPx (at
/// proportionally higher fps to hold the same gap-free ceiling) -- there is
/// no configuration that reaches literal 0% against real (non-synthetic)
/// footage, only diminishing residual error. The real Basler camera MUST
/// be configured to genuinely deliver both these numbers (AcquisitionFrameRate
/// and the ROI/mounting/lens combination that yields ~15cm/frame) -- SimulatedFramesPerSecond/
/// SimulatedFrameHeightPx below are only correct once confirmed against
/// the actual camera's datasheet and on-site mounting.</summary>
public class BaslerCameraOptions
{
    /// <summary>"pypylon"-equivalent here is "pylon"; "simulated" for local dev/testing without hardware.</summary>
    public string Source { get; set; } = "simulated";
    public string DeviceSerial { get; set; } = "";
    public string TestImagePath { get; set; } = "";
    /// <summary>Simulated-mode only: paces GrabFrame() to mimic a real
    /// sensor's frame rate instead of returning every frame instantly (see
    /// SimulatedAreaScanCamera's remarks). Set to 200 to match the
    /// validated 30 km/h target (see this class's own remarks) -- replace
    /// with the real camera's actual configured AcquisitionFrameRate once
    /// confirmed.</summary>
    public int SimulatedFramesPerSecond { get; set; } = 200;

    /// <summary>Simulated-mode only: how many rows tall each grabbed frame
    /// is -- mimics the real camera's configured frame Height (ROI). Set to
    /// 75 to match the validated 30 km/h target (see this class's own
    /// remarks) -- replace with the real camera's actual configured Height
    /// once confirmed.</summary>
    public int SimulatedFrameHeightPx { get; set; } = 75;
}

/// <summary>The interlock config for one lane: a row of N full-lane-width
/// inductive loops (not a wheel touching a tyre -- see ILaneInterlock's
/// remarks for why) plus the LED, all on the same box/PLC. This site has 2
/// loops (entry + exit). Populated from the lane's Controller row (see
/// LaneRuntimeConfigLoader) -- not bound from JSON any more, so the
/// config-binder gotcha this class used to work around no longer
/// applies.</summary>
public class InterlockOptions
{
    /// <summary>"tcp" for the real on-site interlock (a small microcontroller
    /// that reads the loops and drives the LED itself -- see
    /// TcpLaneInterlock's remarks for the wire protocol); "simulated" for
    /// local dev/testing.</summary>
    public string Source { get; set; } = "simulated";

    /// <summary>Cumulative distance (metres) of each loop from the first
    /// one, ascending -- [0, 3.0] for this site's real 2-loop (entry+exit)
    /// interlock. At least 2 entries; a site with more loops in between
    /// would get speed re-measured per segment (see ILaneInterlock
    /// remarks), but that's not this site's hardware. Parsed from
    /// Controller.LoopPositionsMetres (a comma-separated string column, e.g.
    /// "0,3") by LaneRuntimeConfigLoader -- falls back to [0, 3] there if
    /// that column is empty.</summary>
    public List<double> LoopPositionsMetres { get; set; } = new();

    /// <summary>"tcp" interlock only. TCP port the loop controller (a
    /// custom microcontroller, e.g. STM32) listens on -- this PC connects
    /// OUT to it as a client. See TcpLaneInterlock's remarks for the exact
    /// wire protocol.</summary>
    public int TcpPort { get; set; } = 9800;

    /// <summary>"tcp" interlock only. The controller's own static LAN IP
    /// address -- this PC dials out to ControllerIp:TcpPort and keeps
    /// retrying the connection if it's ever unreachable, rather than
    /// listening for the controller to connect to us.</summary>
    public string ControllerIp { get; set; } = "";

    /// <summary>Real interlock only. A loop input must read continuously
    /// "on" for this long before counting as a genuine trip -- inductive
    /// loop detector outputs can chatter for a few ms as a vehicle's
    /// underside passes over, and this debounce avoids a single noise
    /// blip registering as two separate vehicles.</summary>
    public int DebounceMs { get; set; } = 50;

    public double SimulatedIdleSeconds { get; set; } = 20;

    /// <summary>Simulated-mode only: seconds for each segment between
    /// consecutive loops -- must have LoopPositionsMetres.Count - 1
    /// entries. Vary these to simulate a vehicle that speeds up or slows
    /// down mid-pass. Left empty by default for the same config-binder
    /// reason as LoopPositionsMetres above.</summary>
    public List<double> SimulatedSegmentSeconds { get; set; } = new();
}
