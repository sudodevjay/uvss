using UvssService.Detection;

namespace UvssService.Lanes;

/// <summary>Live, observable state for one lane -- LaneWorkerHostedService
/// publishes to this, Lane.razor subscribes to `Changed` and re-renders.
/// This is what makes the Blazor Server UI "live" without a hand-rolled
/// SignalR hub: Blazor Server components already run over their own
/// SignalR connection, so raising a plain C# event and calling
/// StateHasChanged() in the handler is all that's needed.</summary>
public class LaneState
{
    public string LaneName { get; }

    public bool BarrierOpen { get; private set; }
    public string Status { get; private set; } = "Idle";

    /// <summary>Live, per-loop engaged/disengaged state (index 0 = entry
    /// loop, last index = exit loop -- see ILaneInterlock's own remarks),
    /// refreshed continuously by LaneWorkerHostedService's loop-status
    /// poller regardless of whether a pass is in progress. Empty until the
    /// first poll comes in.</summary>
    public IReadOnlyList<bool> LoopEngaged { get; private set; } = Array.Empty<bool>();
    public bool EntryLoopEngaged => LoopEngaged.Count > 0 && LoopEngaged[0];
    public bool ExitLoopEngaged => LoopEngaged.Count > 0 && LoopEngaged[^1];

    public byte[]? DriverFrameJpeg { get; private set; }
    public byte[]? AnprFrameJpeg { get; private set; }
    public byte[]? OverviewFrameJpeg { get; private set; }
    public byte[]? UnderVehicleImageJpeg { get; private set; }

    /// <summary>The driver/ANPR/overview frames frozen at the moment the
    /// LAST completed pass started (see LaneWorkerHostedService's
    /// passDriverJpeg/passAnprJpeg/passOverviewJpeg) -- kept separately from
    /// the continuously-live DriverFrameJpeg/AnprFrameJpeg/OverviewFrameJpeg
    /// above so the dashboard can show a frozen, self-consistent vehicle
    /// (matching PlateText, which is also from that same last pass) while
    /// idle, instead of the raw live feed -- which, in simulation, keeps
    /// cycling to a different stock vehicle the instant the pass ends and
    /// would otherwise show a mismatched vehicle next to that plate.</summary>
    public byte[]? LastPassDriverFrameJpeg { get; private set; }
    public byte[]? LastPassAnprFrameJpeg { get; private set; }
    public byte[]? LastPassOverviewFrameJpeg { get; private set; }

    /// <summary>Same scan as UnderVehicleImageJpeg with any foreign-object
    /// detector hits painted on as translucent regions -- null until a pass
    /// finishes with at least one detection (there's nothing to highlight
    /// during a live/growing pass, since detection only runs on the final
    /// stitched image).</summary>
    public byte[]? HighlightedUnderVehicleImageJpeg { get; private set; }

    public double LastSpeedMetresPerSecond { get; private set; }
    public double LastPassDurationSeconds { get; private set; }
    public int LastRowCount { get; private set; }
    public List<ForeignObjectDetection> LastDetections { get; private set; } = new();
    public bool DetectorAvailable { get; private set; }

    public string PlateText { get; private set; } = "";
    public float PlateConfidence { get; private set; }
    public bool PlateOcrAvailable { get; private set; }
    public byte[]? PlateCropJpeg { get; private set; }
    public string PlateStateCode { get; private set; } = "";
    public string PlateStateName { get; private set; } = "";
    public bool PlateTextValid { get; private set; }
    public int AggregationTextVotes { get; private set; }
    public int AggregationWindowSize { get; private set; }
    public bool AggregationMinCandidatesMet { get; private set; }

    public event Action? Changed;

    public LaneState(string laneName)
    {
        LaneName = laneName;
    }

    public void SetBarrier(bool open)
    {
        BarrierOpen = open;
        Notify();
    }

    public void SetStatus(string status)
    {
        Status = status;
        Notify();
    }

    /// <summary>Called continuously (every ~150-200ms, idle or not) by the
    /// loop-status poller -- only raises Changed when something actually
    /// flipped, so a live-but-unchanged loop state doesn't force a
    /// StateHasChanged() re-render on every single poll tick.</summary>
    public void SetLoopEngaged(IReadOnlyList<bool> engaged)
    {
        if (LoopEngaged.SequenceEqual(engaged))
        {
            return;
        }
        LoopEngaged = engaged;
        Notify();
    }

    public void SetDriverFrame(byte[]? jpeg)
    {
        DriverFrameJpeg = jpeg;
        Notify();
    }

    public void SetAnprFrame(byte[]? jpeg)
    {
        AnprFrameJpeg = jpeg;
        Notify();
    }

    public void SetOverviewFrame(byte[]? jpeg)
    {
        OverviewFrameJpeg = jpeg;
        Notify();
    }

    public void SetUnderVehicleImage(byte[]? jpeg)
    {
        UnderVehicleImageJpeg = jpeg;
        Notify();
    }

    public void SetHighlightedUnderVehicleImage(byte[]? jpeg)
    {
        HighlightedUnderVehicleImageJpeg = jpeg;
        Notify();
    }

    public void SetPassResult(double speedMetresPerSecond, double passDurationSeconds, int rowCount,
        List<ForeignObjectDetection> detections, bool detectorAvailable)
    {
        LastSpeedMetresPerSecond = speedMetresPerSecond;
        LastPassDurationSeconds = passDurationSeconds;
        LastRowCount = rowCount;
        LastDetections = detections;
        DetectorAvailable = detectorAvailable;
        Notify();
    }

    public void SetLastPassFrames(byte[]? driverJpeg, byte[]? anprJpeg, byte[]? overviewJpeg)
    {
        LastPassDriverFrameJpeg = driverJpeg;
        LastPassAnprFrameJpeg = anprJpeg;
        LastPassOverviewFrameJpeg = overviewJpeg;
        Notify();
    }

    public void SetPlateResult(
        string text, float confidence, bool ocrAvailable, string stateCode = "", string stateName = "",
        bool textValid = false, int textVotes = 0, int windowSize = 0, bool minCandidatesMet = false,
        byte[]? plateCropJpeg = null)
    {
        PlateText = text;
        PlateConfidence = confidence;
        PlateOcrAvailable = ocrAvailable;
        PlateCropJpeg = plateCropJpeg;
        PlateStateCode = stateCode;
        PlateStateName = stateName;
        PlateTextValid = textValid;
        AggregationTextVotes = textVotes;
        AggregationWindowSize = windowSize;
        AggregationMinCandidatesMet = minCandidatesMet;
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}

/// <summary>Registry so Blazor pages can look up a lane's state by name.</summary>
public class LaneStateStore
{
    private readonly Dictionary<string, LaneState> _states = new();

    public LaneState GetOrCreate(string laneName)
    {
        if (!_states.TryGetValue(laneName, out var state))
        {
            state = new LaneState(laneName);
            _states[laneName] = state;
        }
        return state;
    }

    public IReadOnlyCollection<LaneState> All => _states.Values;
}
