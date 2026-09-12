using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using UvssService.Cameras;
using UvssService.Config;
using UvssService.Data;
using UvssService.Detection;
using UvssService.Imaging;
using UvssService.Ocr;
using UvssService.Sensors;
using UvssService.Storage;

namespace UvssService.Lanes;

/// <summary>Per-lane UVSS state machine, one background Task per configured
/// lane (the .NET analogue of the Python version's per-lane thread):
///
///   Idle --Loop A trips--> Active --Loop B trips--> Finalize --> Idle
///
/// Active: LED on, driver+ANPR cameras poll continuously (feeding the live
/// UI), the area-scan camera grabs frames as fast as it can.
/// Finalize: LED off, stitch + rescale the frames by the loop-pair speed
/// estimate, run the foreign-object detector, save everything, and record
/// the event -- all in-process (this service and its Blazor UI are one
/// self-contained app, so there's no separate backend to notify).</summary>
public class LaneWorkerHostedService : BackgroundService
{
    private readonly LaneStateStore _stateStore;
    private readonly CameraDefaultsOptions _cameraDefaults;
    private readonly AreaScanOptions _areaScan;
    private readonly LoopSensorDefaultsOptions _loopDefaults;
    private readonly ForeignObjectDetectorOptions _detectorOptions;
    private readonly StorageOptions _storage;
    private readonly List<LaneOptions> _lanes;
    private readonly IPlateDetector _plateDetector;
    private readonly PlateCandidateSearch _plateSearch;
    private readonly PlateOcrOptions _plateOcrOptions;
    private readonly IFaceDetector _faceDetector;
    private readonly FaceDetectorOptions _faceDetectorOptions;
    private readonly AnprFrameHub _anprFrameHub;
    private readonly DriverFrameHub _driverFrameHub;
    private readonly UvssService.Cameras.SimulatedVehicleSlotStore _vehicleSlotStore;
    private readonly IDbContextFactory<UvssDbContext> _dbFactory;
    private readonly ScanEventNotifier _scanEventNotifier;
    private readonly VehicleRegistryStore _vehicleRegistry;
    private readonly UvssService.Sensors.LaneInterlockRegistry _interlockRegistry;
    private readonly UvssService.Cameras.AreaScanCameraRegistry _areaScanCameraRegistry;
    private int _simulatedPassCount;

    /// <summary>How long after auto-opening the barrier for an Approved
    /// vehicle before this side treats it as closed again in LaneState (for
    /// the dashboard's own display only). This is NOT a close command --
    /// the barrier relay itself is wired into auto-close mode and drops
    /// back to closed on its own ~3s after being opened; software never
    /// sends a close. Matches that hardware timing so the UI doesn't show
    /// "open" longer than the barrier physically is.</summary>
    private static readonly TimeSpan BarrierAutoCloseDelay = TimeSpan.FromSeconds(3);

    public LaneWorkerHostedService(
        LaneConfigStore laneConfigStore,
        IOptions<CameraDefaultsOptions> cameraDefaults,
        IOptions<AreaScanOptions> areaScan,
        IOptions<LoopSensorDefaultsOptions> loopDefaults,
        IOptions<ForeignObjectDetectorOptions> detectorOptions,
        IOptions<StorageOptions> storage,
        IOptions<PlateOcrOptions> plateOcrOptions,
        IOptions<FaceDetectorOptions> faceDetectorOptions,
        IPlateDetector plateDetector,
        PlateCandidateSearch plateSearch,
        IFaceDetector faceDetector,
        AnprFrameHub anprFrameHub,
        DriverFrameHub driverFrameHub,
        UvssService.Cameras.SimulatedVehicleSlotStore vehicleSlotStore,
        IDbContextFactory<UvssDbContext> dbFactory,
        ScanEventNotifier scanEventNotifier,
        VehicleRegistryStore vehicleRegistry,
        UvssService.Sensors.LaneInterlockRegistry interlockRegistry,
        UvssService.Cameras.AreaScanCameraRegistry areaScanCameraRegistry,
        LaneStateStore stateStore)
    {
        _stateStore = stateStore;
        _cameraDefaults = cameraDefaults.Value;
        _areaScan = areaScan.Value;
        _loopDefaults = loopDefaults.Value;
        _detectorOptions = detectorOptions.Value;
        _storage = storage.Value;
        _plateOcrOptions = plateOcrOptions.Value;
        _faceDetectorOptions = faceDetectorOptions.Value;
        _lanes = laneConfigStore.Lanes.Where(l => l.Enabled).ToList();
        _plateDetector = plateDetector;
        _plateSearch = plateSearch;
        _faceDetector = faceDetector;
        _anprFrameHub = anprFrameHub;
        _driverFrameHub = driverFrameHub;
        _vehicleSlotStore = vehicleSlotStore;
        _dbFactory = dbFactory;
        _scanEventNotifier = scanEventNotifier;
        _vehicleRegistry = vehicleRegistry;
        _interlockRegistry = interlockRegistry;
        _areaScanCameraRegistry = areaScanCameraRegistry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_lanes.Count == 0)
        {
            Console.WriteLine("No enabled UVSS lanes found.");
            return;
        }

        var detector = BuildForeignObjectDetector();

        var runtimes = _lanes.Select(lane => BuildRuntime(lane, detector)).ToList();

        Console.WriteLine($"Running {runtimes.Count} UVSS lane worker(s).");
        var passTasks = runtimes.Select(lane => RunLaneAsync(lane, stoppingToken));
        var loopStatusTasks = runtimes.Select(lane => PollLoopStatusAsync(lane, stoppingToken));
        await Task.WhenAll(passTasks.Concat(loopStatusTasks));
    }

    /// <summary>How often the entry/exit loop status badges refresh on the
    /// dashboard -- runs independently of RunLaneAsync's Idle/Active pass
    /// state machine (idle or not, this keeps polling), since a stuck-engaged
    /// loop with no vehicle actually present is exactly the kind of sensor
    /// fault an operator needs to see even when no pass is running.</summary>
    private static readonly TimeSpan LoopStatusPollInterval = TimeSpan.FromMilliseconds(150);

    private static async Task PollLoopStatusAsync(LaneRuntime lane, CancellationToken ct)
    {
        var loopCount = lane.Interlock.LoopPositionsMetres.Count;
        var states = new bool[loopCount];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < loopCount; i++)
                {
                    states[i] = await lane.Interlock.IsLoopEngagedAsync(i, ct);
                }
                lane.State.SetLoopEngaged(states.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{lane.Name}] loop-status poll failed: {ex.Message}");
            }
            await Task.Delay(LoopStatusPollInterval, ct).ContinueWith(_ => { });
        }
    }

    private IForeignObjectDetector BuildForeignObjectDetector()
    {
        if (!_detectorOptions.Enabled)
        {
            return new NullForeignObjectDetector();
        }
        return _detectorOptions.Method.Trim().ToLowerInvariant() switch
        {
            "patchcore" => new PatchCoreAnomalyDetector(
                ResolvePath(_detectorOptions.PatchCoreBackbonePath),
                ResolvePath(_detectorOptions.PatchCoreMemoryBankPath),
                _detectorOptions.PatchCoreAnomalyThreshold),
            _ => new OnnxForeignObjectDetector(ResolvePath(_detectorOptions.ModelPath), _detectorOptions.Confidence),
        };
    }

    private LaneRuntime BuildRuntime(LaneOptions lane, IForeignObjectDetector detector)
    {
        var interlock = LaneInterlockFactory.Create(lane.Name, lane.Interlock);
        // So the Lane Setup page's "Test Barrier Relay" button can reach
        // this exact running instance by lane name (see
        // LaneInterlockRegistry's own remarks) -- registered as soon as
        // it's built, before this lane's first pass ever runs.
        _interlockRegistry.Register(lane.Name, interlock);

        var areaScanCamera = AreaScanCameraFactory.Create(
            lane.BaslerCamera, _areaScan.FrameWidthPx, lane.BaslerCamera.SimulatedFrameHeightPx,
            _vehicleSlotStore.GetOrCreate(lane.Name));
        // So Lane Setup's UVSS Camera Status column can read .Available by
        // lane name (see AreaScanCameraRegistry's own remarks).
        _areaScanCameraRegistry.Register(lane.Name, areaScanCamera);

        return new LaneRuntime
        {
            Name = lane.Name,
            State = _stateStore.GetOrCreate(lane.Name),
            Interlock = interlock,
            AreaScanCamera = areaScanCamera,
            // CorrelationResampler's software-side de-duplication corrects
            // for a real camera free-running at a fixed frame rate (see its
            // own remarks) -- there's no hardware encoder at this site, so
            // this always runs for a real ("pylon") camera. Simulated
            // cameras slice a static test image at a perfectly constant
            // simulated rate -- there's no genuine speed variation to
            // correct, so this stays off for those (see the DEMO NOTE this
            // replaces).
            UseCorrelationResampling = lane.BaslerCamera.Source.Trim().Equals("pylon", StringComparison.OrdinalIgnoreCase),
            Detector = detector,
            MaxPassSeconds = _loopDefaults.MaxPassSeconds,
            MaxFramesPerPass = _areaScan.MaxFramesPerPass,
            ReferenceRowsPerMetre = _areaScan.ReferenceRowsPerMetre,
            StorageEnabled = _storage.Enabled,
            DriverImageDir = ResolvePath(_storage.DriverImageDir),
            AnprImageDir = ResolvePath(_storage.AnprImageDir),
            UnderVehicleImageDir = ResolvePath(_storage.UndervehicleImageDir),
            PlateDetector = _plateDetector,
            PlateSearch = _plateSearch,
            TrackMaxFrames = _plateOcrOptions.TrackMaxFrames,
            AggregationEnabled = _plateOcrOptions.AggregationEnabled,
            AggregationWindowSize = _plateOcrOptions.AggregationWindowSize,
            AggregationMinCandidates = _plateOcrOptions.AggregationMinCandidates,
            FaceDetector = _faceDetector,
            FaceTrackMaxFrames = _faceDetectorOptions.TrackMaxFrames,
        };
    }

    private async Task RunLaneAsync(LaneRuntime lane, CancellationToken ct)
    {
        Console.WriteLine($"[{lane.Name}] UVSS lane worker started (areascan available={lane.AreaScanCamera.Available}).");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnePassAsync(lane, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{lane.Name}] pass failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task RunOnePassAsync(LaneRuntime lane, CancellationToken ct)
    {
        var entryTime = await lane.Interlock.WaitForEntryAsync(ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }
        Console.WriteLine($"[{lane.Name}] loop 0 tripped -- vehicle entering.");
        lane.State.SetStatus("Active");

        // Advance to a fresh simulated vehicle for this pass (see
        // SimulatedVehicleSlot's own remarks for why this replaced a
        // wall-clock timer) and give the driver/ANPR/overview cameras' own
        // poll loops (StreamIntervalMs, default 150ms) a brief moment to
        // actually pick up the new slot before freezing a snapshot of it --
        // otherwise the very first read below could still catch one last
        // stale poll of the PREVIOUS vehicle.
        _vehicleSlotStore.GetOrCreate(lane.Name).Advance();
        await Task.Delay(300, ct);

        // Freeze the ANPR/overview cameras' frames right now, at
        // vehicle-entry, rather than reading lane.State.*FrameJpeg again
        // later at finalize time. Those cameras stream continuously and
        // independently of this pass (see CameraStreamingHostedService), so
        // by the time a pass finishes (several seconds later, plus whatever
        // finalize/OCR delay), they may already be showing a DIFFERENT
        // simulated vehicle -- which is exactly what caused the stored
        // snapshot images (and posted event) to show a mismatched vehicle
        // from the plate that PlateTrack captured starting from this same
        // moment. Freezing here keeps everything for this pass anchored to
        // one consistent instant. (The driver frame gets a smarter,
        // multi-frame best-shot pick instead -- see driverTrack below --
        // but passDriverJpeg is kept as a fallback for when that comes back
        // empty, e.g. the face detector found nothing all pass.)
        var passDriverJpeg = lane.State.DriverFrameJpeg;
        var passAnprJpeg = lane.State.AnprFrameJpeg;
        var passOverviewJpeg = lane.State.OverviewFrameJpeg;

        // Fresh, pass-scoped tracks -- NOT fields shared across passes.
        // FinalizePass (including the multi-frame OCR aggregation) now
        // runs in the background (see below) so it can still be reading
        // this pass's frames while the NEXT pass has already started and
        // is collecting its own; sharing one track object across passes
        // would let a fast-arriving next vehicle reset/corrupt the frames
        // still being OCR'd for the previous one.
        // The Driver/ANPR/Overview cameras stream continuously regardless of
        // Active/Idle (see CameraStreamingHostedService) -- this pass only
        // taps into the already-running feeds for its duration, rather
        // than starting/stopping the cameras themselves.
        var plateTrack = new PlateTrack(lane.PlateDetector, lane.TrackMaxFrames);
        _anprFrameHub.Subscribe(lane.Name, frame => plateTrack.AddFrame(frame));
        var driverTrack = new DriverTrack(lane.FaceDetector, lane.FaceTrackMaxFrames);
        _driverFrameHub.Subscribe(lane.Name, frame => driverTrack.AddFrame(frame));

        var capture = await CapturePassAsync(lane, entryTime, ct);
        _anprFrameHub.Unsubscribe(lane.Name);
        _driverFrameHub.Unsubscribe(lane.Name);

        if (!capture.Completed)
        {
            // LoopTimes.Count == 1 means only entry ever engaged (exit
            // never engaged at all within MaxPassSeconds) -- the original
            // stuck-sensor story. LoopTimes.Count == 2 means the exit loop
            // DID engage (a vehicle really did reach it) but never cleared
            // within MaxPassSeconds -- a different, more specific fault:
            // something is physically still sitting on/over the exit loop
            // (a stopped vehicle) or the sensor is stuck reporting engaged.
            var faultReason = capture.LoopTimes.Count > 1
                ? $"exit loop engaged but never cleared within {lane.MaxPassSeconds}s -- vehicle stopped on/over it, or a stuck sensor"
                : $"final loop never engaged within {lane.MaxPassSeconds}s -- stuck loop or vehicle stopped before reaching it";
            Console.WriteLine($"[{lane.Name}] {faultReason} -- treating as a sensor fault and resetting.");
            lane.State.SetStatus("Idle");
            plateTrack.Reset();
            driverTrack.Reset();
            foreach (var frame in capture.Frames)
            {
                frame.Dispose();
            }
            return;
        }

        // The vehicle's physical presence on this lane is done (LED is
        // already off) -- return to Idle and let the interlock start
        // watching for the NEXT vehicle immediately. Image stitching,
        // foreign-object detection, storage, and especially the multi-frame
        // OCR aggregation (up to AggregationWindowSize SAR decode calls,
        // the slowest step in this whole pipeline) run in the background
        // instead of blocking this lane from being ready for whoever's
        // next -- exactly the "everything should run in parallel, that's
        // why there are separate cameras for each job" principle this
        // service is built around.
        lane.State.SetStatus("Idle");
        _ = RunFinalizeInBackgroundAsync(lane, capture, plateTrack, driverTrack, passDriverJpeg, passAnprJpeg, passOverviewJpeg);
    }

    private async Task RunFinalizeInBackgroundAsync(
        LaneRuntime lane, CaptureResult capture, PlateTrack plateTrack, DriverTrack driverTrack,
        byte[]? passDriverJpeg, byte[]? passAnprJpeg, byte[]? passOverviewJpeg)
    {
        try
        {
            // FinalizePass is pure CPU-bound work (stitching, detection, OCR
            // aggregation) now that it no longer POSTs anywhere -- Task.Run
            // is what actually pushes it onto a threadpool thread so this
            // stays "background" (the lane is already back to Idle and must
            // not be blocked waiting for this), rather than running
            // synchronously on whatever thread called this method.
            await Task.Run(() => FinalizePass(lane, capture, plateTrack, driverTrack, passDriverJpeg, passAnprJpeg, passOverviewJpeg));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{lane.Name}] background finalize failed: {ex.Message}");
        }
        finally
        {
            plateTrack.Reset();
            driverTrack.Reset();
            // Every individual grabbed frame (capture.Frames) was consumed by
            // FrameStitcher.Stitch inside FinalizePass via VConcat (which
            // always copies into a new Mat, never takes ownership) -- these
            // were the single largest leak found in this pass, since a long
            // pass can capture well over a hundred of these Mats.
            foreach (var frame in capture.Frames)
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>How often the live under-vehicle preview is pushed to the UI
    /// while a pass is in progress -- the whole point being that the image
    /// visibly builds up top-to-bottom as the vehicle drives over, the same
    /// way a real UVSS operator console works, instead of only appearing
    /// once the vehicle has already left (by which point it's too late for
    /// anyone watching to react to something suspicious).</summary>
    private static readonly TimeSpan LiveStitchUpdateInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Result of one capture pass: every grabbed frame, the ENGAGE
    /// trip time of every loop (index 0 = entry), which frame index had just
    /// been captured when each loop engaged -- `FrameIndexAtLoop[i]` to
    /// `FrameIndexAtLoop[i+1]` is exactly the segment of frames captured
    /// between loop i engaging and loop i+1 engaging, which FinalizePass
    /// rescales using that segment's own measured speed -- and when the
    /// EXIT loop (the last one) actually cleared (went back to disengaged).
    /// `Frames` keeps growing past `FrameIndexAtLoop[^1]` all the way to
    /// ExitClearTime -- see CapturePassAsync's own remarks for why: that
    /// tail is exactly the part of a vehicle longer than the entry-exit loop
    /// spacing that hadn't finished passing under the camera yet when the
    /// exit loop first engaged.
    ///
    /// LoopTimes.Count tells you how far a pass got before a fault:
    /// still just [entryTime] means the exit loop never engaged at all;
    /// 2 entries with Completed==false means it engaged but never cleared
    /// (ExitClearTime stays null) -- two different fault stories, logged
    /// differently by RunOnePassAsync.</summary>
    private record CaptureResult(List<Mat> Frames, List<DateTime> LoopTimes, List<int> FrameIndexAtLoop, DateTime? ExitClearTime, bool Completed);

    private static async Task<CaptureResult> CapturePassAsync(LaneRuntime lane, DateTime entryTime, CancellationToken ct)
    {
        lane.AreaScanCamera.Start();
        var frames = new List<Mat>();
        var pendingFrames = new List<Mat>();
        var loopTimes = new List<DateTime> { entryTime };
        var frameIndexAtLoop = new List<int> { 0 };
        Mat? liveStitch = null;
        var lastUiUpdate = DateTime.UtcNow;
        var totalLoops = lane.Interlock.LoopPositionsMetres.Count;
        var exitLoopIndex = totalLoops - 1;
        var currentLoopIndex = 0;
        // Once the exit loop ENGAGES (currentLoopIndex reaches exitLoopIndex),
        // capture does NOT stop there -- a vehicle longer than the physical
        // entry-exit loop spacing (which is every ordinary vehicle at a
        // typical few-metre spacing, let alone a bus/truck) still has most
        // of its underside yet to pass under the camera at that instant.
        // Instead this switches to polling IsLoopEngagedAsync for the exit
        // loop going back to disengaged (the vehicle's REAR has now cleared
        // it too) -- only THEN is the whole vehicle guaranteed to have
        // passed under the camera, regardless of how long it is.
        var exitEngaged = false;
        DateTime? exitClearTime = null;
        var completed = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var elapsed = (DateTime.UtcNow - entryTime).TotalSeconds;
                if (elapsed >= lane.MaxPassSeconds || frames.Count >= lane.MaxFramesPerPass)
                {
                    break;
                }

                var frame = lane.AreaScanCamera.GrabFrame(200);
                if (frame != null)
                {
                    frames.Add(frame);
                    pendingFrames.Add(frame);
                }

                if (pendingFrames.Count > 0 && DateTime.UtcNow - lastUiUpdate >= LiveStitchUpdateInterval)
                {
                    liveStitch = AppendFramesDisposingOld(liveStitch, pendingFrames);
                    pendingFrames.Clear();
                    lane.State.SetUnderVehicleImage(EncodeUnderVehicleJpeg(liveStitch));
                    lastUiUpdate = DateTime.UtcNow;
                }

                if (!exitEngaged)
                {
                    var nextLoopTime = await lane.Interlock.WaitForLoopAsync(currentLoopIndex + 1, loopTimes[currentLoopIndex], TimeSpan.Zero, ct);
                    if (nextLoopTime != null)
                    {
                        currentLoopIndex++;
                        loopTimes.Add(nextLoopTime.Value);
                        frameIndexAtLoop.Add(frames.Count);
                        Console.WriteLine($"[{lane.Name}] loop {currentLoopIndex} engaged -- {frames.Count} frames so far.");
                        if (currentLoopIndex == exitLoopIndex)
                        {
                            exitEngaged = true;
                        }
                    }
                }
                else if (!await lane.Interlock.IsLoopEngagedAsync(exitLoopIndex, ct))
                {
                    exitClearTime = DateTime.UtcNow;
                    completed = true;
                    Console.WriteLine($"[{lane.Name}] exit loop cleared -- {frames.Count} frames total, vehicle fully passed.");
                    break;
                }
            }

            // Flush whatever's left unpublished so the live view is caught up
            // to the very last grabbed frame before FinalizePass takes
            // over with the properly speed-rescaled version.
            if (pendingFrames.Count > 0)
            {
                liveStitch = AppendFramesDisposingOld(liveStitch, pendingFrames);
                lane.State.SetUnderVehicleImage(EncodeUnderVehicleJpeg(liveStitch));
            }
        }
        finally
        {
            lane.AreaScanCamera.Stop();
            // The last liveStitch built here was only ever used to encode a
            // JPEG for the live preview -- FinalizePass re-stitches
            // from capture.Frames independently (with proper speed
            // rescaling), so this copy is never read again and would
            // otherwise leak.
            liveStitch?.Dispose();
        }
        return new CaptureResult(frames, loopTimes, frameIndexAtLoop, exitClearTime, completed);
    }

    /// <summary>AppendFrames always returns a brand-new Mat (VConcat never
    /// writes in place) -- called on every live-preview tick (every 150ms
    /// during an Active pass), so without disposing the Mat it's replacing,
    /// this leaked one growing-sized native buffer per tick, the single
    /// biggest source of the multi-GB/hour memory leak this was fixed
    /// for.</summary>
    private static Mat? AppendFramesDisposingOld(Mat? existing, List<Mat> newFrames)
    {
        var appended = AppendFrames(existing, newFrames);
        if (existing != null && !ReferenceEquals(existing, appended))
        {
            existing.Dispose();
        }
        return appended;
    }

    /// <summary>Appends `newFrames` onto the already-stitched `existing`
    /// image (or just stitches `newFrames` if this is the first batch) --
    /// grows the live preview incrementally rather than re-stitching every
    /// frame captured so far on every UI update tick.</summary>
    private static Mat? AppendFrames(Mat? existing, List<Mat> newFrames)
    {
        var toConcat = new List<Mat>();
        if (existing != null)
        {
            toConcat.Add(existing);
        }
        toConcat.AddRange(newFrames.Where(frame => frame != null && !frame.Empty()));
        if (toConcat.Count == 0)
        {
            return existing;
        }
        var merged = new Mat();
        Cv2.VConcat(toConcat.ToArray(), merged);
        return merged;
    }

    /// <summary>CorrelationResampler's own defaults (chunkHeight=2,
    /// templateHeight=1) were tuned for a true line-scan sensor, where every
    /// grab is a single pixel row -- an area-scan camera grabs whole
    /// FrameHeightPx-tall frames instead, and empirically (see
    /// AreaScanSpeedSweepTest's --test-area-scan-tune/-confidence sweeps)
    /// those line-scan-shaped defaults are not just suboptimal but badly
    /// wrong here: 100-600%+ reconstruction error across realistic
    /// constant/variable-speed profiles, because the resampler's internal
    /// chunking has to align with the ACTUAL per-grab frame size to find
    /// genuine frame-to-frame overlap at all. The sweep found a chunk size
    /// tied to the source frames' own actual height reduces worst-case
    /// error to single digits: chunkHeight = the frames' own height (so
    /// each correlation step evaluates one real camera grab at a time), and
    /// templateHeight = a quarter of that -- a template much LARGER than
    /// that (e.g. half, tried first) fails silently as vehicle speed rises,
    /// because real frame-to-frame overlap shrinks with speed and a
    /// too-large template stops existing anywhere in the new frame well
    /// before the camera's hard gap-free ceiling is reached. minConfidence
    /// is relaxed slightly from the line-scan default (0.95 -> 0.85) since
    /// the sweep found it stops being the sensitive parameter once the
    /// chunk/template sizes are actually right, and a hair more tolerance
    /// helps against real (noisier) footage than clean synthetic test
    /// texture.</summary>
    private static Mat ResampleAreaScanFrames(Mat stitched, IReadOnlyList<Mat> sourceFrames)
    {
        var usableFrames = sourceFrames.Where(frame => frame != null && !frame.Empty()).ToList();
        if (usableFrames.Count == 0)
        {
            return stitched;
        }
        // The largest observed grab height is the camera's real per-grab
        // frame size -- a shorter one can only be a truncated remainder
        // (e.g. the simulated camera running out of test image), never the
        // camera's actual configured frame height.
        var frameHeightPx = usableFrames.Max(frame => frame.Rows);
        var chunkHeight = Math.Max(2, frameHeightPx);
        var templateHeight = Math.Clamp(chunkHeight / 4, 1, chunkHeight - 1);
        return CorrelationResampler.ResampleBySelfCorrelation(stitched, chunkHeight, templateHeight, minConfidence: 0.85);
    }

    /// <summary>Two corrections stack per loop-to-loop segment, each fixing
    /// a different thing:
    ///
    /// 1. CorrelationResampler -- de-duplicates the raw capture using the
    ///    image content itself (see its remarks), fixing the internal
    ///    stretching/streaking a vehicle that sped up, slowed down, or
    ///    briefly stopped *within* this segment would otherwise leave
    ///    behind. Needs zero external timing/distance info.
    /// 2. FrameStitcher.NormalizeHeight -- the de-duplicated image is
    ///    correctly *shaped* but not yet at a known physical scale; this
    ///    calibrates it to real-world rows-per-metre using the segment's
    ///    loop-measured distance and average speed, since no amount of
    ///    image correlation alone can tell you the absolute scale (only the
    ///    loops -- a known physical distance -- can).</summary>
    private async Task FinalizePass(
        LaneRuntime lane, CaptureResult capture, PlateTrack plateTrack, DriverTrack driverTrack,
        byte[]? passDriverJpeg, byte[]? passAnprJpeg, byte[]? passOverviewJpeg)
    {
        // Prefer the best frame DriverTrack saw across the whole pass (face
        // detected, sharp, roughly centered) over the naive single frame
        // frozen at pass-start -- falls back to that frozen frame if the
        // face detector never found anything usable this pass.
        if (driverTrack.BestFrame() is { } bestDriverFrame)
        {
            Cv2.ImEncode(".jpg", bestDriverFrame, out var bestDriverJpeg);
            passDriverJpeg = bestDriverJpeg;
        }

        var positions = lane.Interlock.LoopPositionsMetres;
        var segmentImages = new List<Mat>();
        var segmentSpeeds = new List<double>();

        for (var i = 0; i < positions.Count - 1; i++)
        {
            var segmentFrames = capture.Frames.GetRange(
                capture.FrameIndexAtLoop[i], capture.FrameIndexAtLoop[i + 1] - capture.FrameIndexAtLoop[i]);
            var segmentDistance = positions[i + 1] - positions[i];
            var segmentSpeed = LaneInterlockFactory.EstimateSpeedMetresPerSecond(
                segmentDistance, capture.LoopTimes[i], capture.LoopTimes[i + 1]);
            segmentSpeeds.Add(segmentSpeed);

            var segmentStitched = FrameStitcher.Stitch(segmentFrames);
            if (segmentStitched != null)
            {
                // Simulated cameras slice a real photo at a perfectly
                // constant simulated rate -- there's no genuine speed
                // variation for CorrelationResampler to correct, so it has
                // nothing real to find and would corrupt otherwise-clean
                // content (confirmed via isolated before/after
                // diagnostics). Only applied for a real ("pylon") camera --
                // see LaneRuntime.UseCorrelationResampling in BuildRuntime.
                var deduplicated = lane.UseCorrelationResampling
                    ? ResampleAreaScanFrames(segmentStitched, segmentFrames)
                    : segmentStitched;
                var normalized = FrameStitcher.NormalizeHeight(deduplicated, segmentDistance, lane.ReferenceRowsPerMetre);
                if (!ReferenceEquals(normalized, segmentStitched))
                {
                    segmentStitched.Dispose();
                }
                if (!ReferenceEquals(deduplicated, segmentStitched) && !ReferenceEquals(deduplicated, normalized))
                {
                    deduplicated.Dispose();
                }
                segmentImages.Add(normalized);
            }
        }

        // Tail: everything captured AFTER the exit loop engaged, up until it
        // actually cleared -- exactly the part of a vehicle longer than the
        // entry-exit loop spacing that hadn't finished passing under the
        // camera yet when the exit loop first engaged (see CapturePassAsync's
        // and CaptureResult's own remarks -- this is what makes a bus/truck
        // longer than the loop spacing still get fully scanned instead of
        // being cut off). No loop independently measures this segment's own
        // speed (only entry-to-exit-engage is measured) -- the best
        // available estimate is the last measured segment's speed, assumed
        // roughly constant for this short remainder of the pass.
        var tailFrames = capture.Frames.GetRange(
            capture.FrameIndexAtLoop[^1], capture.Frames.Count - capture.FrameIndexAtLoop[^1]);
        var tailEndTime = capture.ExitClearTime ?? capture.LoopTimes[^1];
        var tailDuration = (tailEndTime - capture.LoopTimes[^1]).TotalSeconds;
        var tailSpeed = segmentSpeeds.Count > 0 ? segmentSpeeds[^1] : 0.0;
        var tailDistance = Math.Max(0.0, tailSpeed * tailDuration);

        if (tailFrames.Count > 0 && tailDistance > 0)
        {
            var tailStitched = FrameStitcher.Stitch(tailFrames);
            if (tailStitched != null)
            {
                var tailDeduplicated = lane.UseCorrelationResampling
                    ? ResampleAreaScanFrames(tailStitched, tailFrames)
                    : tailStitched;
                var tailNormalized = FrameStitcher.NormalizeHeight(tailDeduplicated, tailDistance, lane.ReferenceRowsPerMetre);
                if (!ReferenceEquals(tailNormalized, tailStitched))
                {
                    tailStitched.Dispose();
                }
                if (!ReferenceEquals(tailDeduplicated, tailStitched) && !ReferenceEquals(tailDeduplicated, tailNormalized))
                {
                    tailDeduplicated.Dispose();
                }
                segmentImages.Add(tailNormalized);
            }
        }

        if (segmentImages.Count == 0)
        {
            Console.WriteLine($"[{lane.Name}] no frames captured this pass -- skipping event.");
            return;
        }

        var finalImage = FrameStitcher.Stitch(segmentImages) ?? segmentImages[0];
        foreach (var segmentImage in segmentImages)
        {
            if (!ReferenceEquals(segmentImage, finalImage))
            {
                segmentImage.Dispose();
            }
        }
        var totalDistance = positions[^1] - positions[0] + tailDistance;
        var totalDuration = (tailEndTime - capture.LoopTimes[0]).TotalSeconds;
        var avgSpeed = LaneInterlockFactory.EstimateSpeedMetresPerSecond(totalDistance, capture.LoopTimes[0], tailEndTime);

        Console.WriteLine(
            $"[{lane.Name}] pass complete -- {capture.Frames.Count} frames across {segmentImages.Count} segment(s) "
            + $"(incl. {tailFrames.Count}-frame/{tailDistance:F2}m tail past the exit loop), "
            + $"per-segment speed=[{string.Join(", ", segmentSpeeds.Select(s => s.ToString("F2")))}] m/s, "
            + $"avg={avgSpeed:F2} m/s, duration={totalDuration:F2}s"
        );

        // Reported downstream (LaneState.LastRowCount, the "row_count"
        // POST field) as the total scan ROWS captured across every grabbed
        // frame -- a proxy for the scan's resolution/length, independent of
        // how many camera grabs (frames) it took to capture it.
        var totalRowsCaptured = capture.Frames.Sum(frame => frame.Rows);

        var detections = lane.Detector.Available ? lane.Detector.Detect(finalImage) : new List<ForeignObjectDetection>();
        if (_detectorOptions.SimulateForTesting && detections.Count == 0 && Interlocked.Increment(ref _simulatedPassCount) % 2 == 0)
        {
            // TEST-ONLY (see ForeignObjectDetectorOptions.SimulateForTesting):
            // every other pass gets one fake detection so both the "clean"
            // and "flagged" dashboard states are reachable without a real
            // trained model. Roughly centered/mid-height on the scan.
            detections.Add(new ForeignObjectDetection(
                "simulated_test_object", 0.9f,
                new Rect(finalImage.Cols / 4, finalImage.Rows / 3, finalImage.Cols / 2, finalImage.Rows / 6)));
        }
        if (detections.Count > 0)
        {
            Console.WriteLine($"[{lane.Name}] foreign-object detector flagged {detections.Count} region(s).");
        }

        var underVehicleJpeg = EncodeUnderVehicleJpeg(finalImage);
        lane.State.SetUnderVehicleImage(underVehicleJpeg);
        lane.State.SetPassResult(avgSpeed, totalDuration, totalRowsCaptured, detections, lane.Detector.Available);
        lane.State.SetLastPassFrames(passDriverJpeg, passAnprJpeg, passOverviewJpeg);

        byte[]? highlightedUnderVehicleJpeg = null;
        if (detections.Count > 0)
        {
            // Highlight regions are drawn on finalImage (pre-rotation) using
            // its own detection coordinates, then pushed through the exact
            // same rotate+encode step as the plain scan -- so the two stay
            // pixel-aligned for the dashboard's side-by-side/stacked view.
            using var highlighted = DrawDetectionHighlights(finalImage, detections);
            highlightedUnderVehicleJpeg = EncodeUnderVehicleJpeg(highlighted);
        }
        lane.State.SetHighlightedUnderVehicleImage(highlightedUnderVehicleJpeg);
        finalImage.Dispose();

        var plateResult = RecognizePlateAcrossTrack(lane, plateTrack);
        var plateOcrAvailable = lane.PlateDetector.Available;
        lane.State.SetPlateResult(
            plateResult.PlateText, (float)plateResult.PlateConfidence, plateOcrAvailable,
            plateResult.PlateStateCode, plateResult.PlateStateName, plateResult.PlateTextValid,
            plateResult.AggregationTextVotes, plateResult.AggregationWindowSize, plateResult.AggregationMinCandidatesMet,
            plateResult.PlateCropJpeg);
        if (!string.IsNullOrEmpty(plateResult.PlateText))
        {
            Console.WriteLine(
                $"[{lane.Name}] plate read: {plateResult.PlateText} (confidence={plateResult.PlateConfidence:F2}, "
                + $"votes={plateResult.AggregationTextVotes}/{plateResult.AggregationWindowSize}, "
                + $"valid={plateResult.PlateTextValid})");
        }

        // Every image that's ever going to exist for this pass is saved to
        // disk here (date-bucketed, see ImageStorage), and its path -- not
        // the bytes themselves -- is what gets recorded in the ScanEvent row
        // below. A null path means that image genuinely doesn't exist for
        // this pass (StorageEnabled=false, no detection -> no highlighted
        // image, no overview camera at this site, etc.), not a save
        // failure.
        string? underVehicleImagePath = null, highlightedImagePath = null, driverImagePath = null,
            anprImagePath = null, overviewImagePath = null, plateCropImagePath = null;
        if (lane.StorageEnabled)
        {
            underVehicleImagePath = ImageStorage.SaveBytes(lane.UnderVehicleImageDir, lane.Name, underVehicleJpeg);
            highlightedImagePath = ImageStorage.SaveBytes(lane.UnderVehicleImageDir, lane.Name, highlightedUnderVehicleJpeg, "highlighted");
            driverImagePath = ImageStorage.SaveBytes(lane.DriverImageDir, lane.Name, passDriverJpeg);
            anprImagePath = ImageStorage.SaveBytes(lane.AnprImageDir, lane.Name, passAnprJpeg);
            overviewImagePath = ImageStorage.SaveBytes(lane.AnprImageDir, lane.Name, passOverviewJpeg, "overview");
            plateCropImagePath = ImageStorage.SaveBytes(lane.AnprImageDir, lane.Name, plateResult.PlateCropJpeg, "platecrop");
        }

        var (driverName, admission, dossierNotes) = DecideAdmission(plateResult.PlateText, detections.Count > 0);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.ScanEvents.Add(new UvssService.Data.Entities.ScanEvent
            {
                Timestamp = DateTime.Now,
                LaneName = lane.Name,
                PlateText = plateResult.PlateText,
                PlateStateName = plateResult.PlateStateName,
                SpeedMetresPerSecond = avgSpeed,
                PlateTextValid = plateResult.PlateTextValid,
                ForeignObjectDetected = detections.Count > 0,
                DriverName = driverName,
                Admission = admission,
                DossierNotes = dossierNotes,
                UnderVehicleImagePath = underVehicleImagePath,
                HighlightedUnderVehicleImagePath = highlightedImagePath,
                DriverImagePath = driverImagePath,
                AnprImagePath = anprImagePath,
                OverviewImagePath = overviewImagePath,
                PlateCropImagePath = plateCropImagePath,
            });
            await db.SaveChangesAsync();
            _scanEventNotifier.NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{lane.Name}] failed to save scan event to the database (is MySQL running?): {ex.Message}");
        }

        // Barrier auto-opens ONLY for a fully Approved vehicle (registered
        // Whitelist AND no foreign-object flag -- see DecideAdmission) --
        // Pending (unregistered/Normal) and Denied (Blacklist or a flagged
        // scan) both require an operator to open it manually instead. By
        // the time this runs the vehicle has already physically cleared the
        // exit loop (FinalizePass only starts once CapturePassAsync
        // completes), so this assumes the barrier itself sits far enough
        // down the lane that the plate-OCR aggregation above has time to
        // finish before the vehicle actually reaches it.
        if (admission == AdmissionStatus.Approved)
        {
            Console.WriteLine($"[{lane.Name}] plate {plateResult.PlateText} Approved -- opening barrier automatically.");
            _ = OpenBarrierTemporarilyAsync(lane);
        }
    }

    /// <summary>Sends the barrier OPEN command and, after
    /// BarrierAutoCloseDelay, just flips LaneState back to closed for the
    /// dashboard -- fire-and-forget from FinalizePass so a slow/stuck relay
    /// write never blocks finalize itself. Deliberately never sends a close
    /// command: the barrier relay is configured in hardware auto-close mode
    /// and drops itself after ~3s.</summary>
    private static async Task OpenBarrierTemporarilyAsync(LaneRuntime lane)
    {
        try
        {
            await lane.Interlock.OpenBarrierAsync();
            lane.State.SetBarrier(true);
            await Task.Delay(BarrierAutoCloseDelay);
            lane.State.SetBarrier(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{lane.Name}] barrier auto-open failed: {ex.Message}");
        }
    }

    /// <summary>Exact C# port of anpr-ai-service's multi-frame plate-OCR
    /// aggregation: takes the best AggregationWindowSize (10) frames the
    /// PlateTrack collected during this pass (already narrowed down from
    /// up to TrackMaxFrames/40 samples by a cheap detector-only visibility
    /// score), runs the full single-frame pipeline (plate detect -> crop ->
    /// CTC multi-crop-variant search -> SAR refinement -- PlateCandidateSearch,
    /// validated bit-identical to anpr-ai-service's recognize_best) on each,
    /// then combines the per-frame reads via per-character consensus voting
    /// (PlateAggregation.SelectAggregatedResult) into one final decided
    /// plate text -- same approach, same weights, as anpr-ai-service's
    /// select_aggregated_result. All native in-process ONNX Runtime calls,
    /// no HTTP call to anpr-ai-service.</summary>
    private static AggregatedPlateResult RecognizePlateAcrossTrack(LaneRuntime lane, PlateTrack plateTrack)
    {
        if (!lane.AggregationEnabled || plateTrack.FrameCount == 0)
        {
            return new AggregatedPlateResult();
        }

        var selectedFrames = plateTrack.SelectedFrames(lane.AggregationWindowSize);
        var results = PlateFrameProcessor.ProcessFrameBatch(selectedFrames, lane.PlateDetector, lane.PlateSearch);
        return PlateAggregation.SelectAggregatedResult(results, lane.AggregationMinCandidates);
    }

    /// <summary>Cross-references the read plate against VehicleRegistryStore
    /// to decide the audit-log's admission verdict -- a detected foreign
    /// object always forces Denied (a real safety override should never be
    /// auto-approved away by a whitelist entry), a Blacklist entry is
    /// Denied, a Whitelist entry is auto-Approved, and anything else
    /// (unregistered, or registered as plain Normal) is left Pending for a
    /// human operator to decide, matching the reference console's own
    /// Approved/Pending/Denied vocabulary.</summary>
    private (string DriverName, AdmissionStatus Admission, string DossierNotes) DecideAdmission(
        string plateText, bool foreignObjectDetected)
    {
        var match = _vehicleRegistry.FindByPlate(plateText);
        var driverName = match?.OwnerName is { Length: > 0 } name ? name : "Unknown";
        var registryNote = match?.Notes ?? "";
        var classification = match?.Classification ?? VehicleClassification.Normal;

        if (foreignObjectDetected)
        {
            var notes = classification == VehicleClassification.Blacklist
                ? $"CRITICAL ALERT: Blacklisted vehicle with a flagged scan region! {registryNote}".Trim()
                : $"THREAT DETECTED: foreign-object scan flagged this pass -- manual inspection required. {registryNote}".Trim();
            return (driverName, AdmissionStatus.Denied, notes);
        }
        if (classification == VehicleClassification.Blacklist)
        {
            return (driverName, AdmissionStatus.Denied, $"CRITICAL ALERT: Blacklisted vehicle! {registryNote}".Trim());
        }
        if (classification == VehicleClassification.Whitelist)
        {
            return (driverName, AdmissionStatus.Approved, $"Auto-Approved: Registered vehicle. {registryNote}".Trim());
        }
        var unregisteredNote = match != null
            ? $"Scan complete. No anomalies detected. {registryNote}".Trim()
            : "Scan complete. No anomalies detected. Vehicle not in registry.";
        return (driverName, AdmissionStatus.Pending, unregisteredNote);
    }

    private static byte[]? EncodeJpeg(Mat image)
    {
        if (image.Empty())
        {
            return null;
        }
        Cv2.ImEncode(".jpg", image, out var buffer);
        return buffer;
    }

    /// <summary>The raw stitched image is captured/stored in "capture
    /// order" -- each new frame stacks underneath the last, so it comes out
    /// tall/portrait (width = the fixed camera frame width across the
    /// vehicle, height = distance travelled). Real UVSS operator displays
    /// (see reference footage) show this landscape instead -- the vehicle's
    /// direction of travel running left-to-right, its across-vehicle width
    /// top-to-bottom -- the natural way to view a chassis from below.
    /// Rotating 90° COUNTER-clockwise (not clockwise) maps "first frame
    /// captured" (front of vehicle) to the LEFT edge at a fixed position,
    /// with each newly-captured frame extending the image further to the
    /// RIGHT -- clockwise would instead keep the newest frame pinned to the
    /// left and shift the whole vehicle rightward on every update, which
    /// looks like the image growing backwards.</summary>
    private static byte[]? EncodeUnderVehicleJpeg(Mat image)
    {
        if (image.Empty())
        {
            return null;
        }
        using var rotated = new Mat();
        Cv2.Rotate(image, rotated, RotateFlags.Rotate90Counterclockwise);

        // The live-growing preview (still-in-progress pass, re-encoded
        // every ~150ms) can reach tens of thousands of pixels wide by the
        // time a long/fast-camera pass ends -- past a certain size this
        // started failing JPEG encoding outright ("can't encode data:
        // unknown exception", seen once frame throughput went up to
        // 200fps) rather than just being slow. The final, saved scan is
        // never anywhere near this large -- FinalizePass normalizes to
        // ~1500 rows before this ever runs -- so downscaling only kicks in
        // for the oversized live-preview case, with no visible loss for
        // the archived result.
        const int maxDimension = 8000;
        var largest = Math.Max(rotated.Cols, rotated.Rows);
        var toEncode = rotated;
        Mat? downscaled = null;
        if (largest > maxDimension)
        {
            var scale = maxDimension / (double)largest;
            downscaled = new Mat();
            Cv2.Resize(rotated, downscaled, new Size((int)(rotated.Cols * scale), (int)(rotated.Rows * scale)));
            toEncode = downscaled;
        }
        var encoded = Cv2.ImEncode(".jpg", toEncode, out var buffer);
        downscaled?.Dispose();
        return encoded && buffer.Length > 0 ? buffer : null;
    }

    /// <summary>Paints each detection's box as a translucent orange fill
    /// plus a solid outline -- the dashboard's second "highlighted" scan
    /// panel, alongside the plain one, so a flagged region is obvious at a
    /// glance without having to compare against the plain scan by eye. Only
    /// boxes are available (the detector doesn't produce segmentation
    /// masks), so this is a rectangular approximation of the region, not a
    /// silhouette.</summary>
    private static Mat DrawDetectionHighlights(Mat source, List<ForeignObjectDetection> detections)
    {
        var overlay = source.Clone();
        foreach (var detection in detections)
        {
            Cv2.Rectangle(overlay, detection.BBox, new Scalar(0, 140, 255), thickness: -1);
        }
        var blended = new Mat();
        Cv2.AddWeighted(overlay, 0.45, source, 0.55, 0, blended);
        overlay.Dispose();
        foreach (var detection in detections)
        {
            Cv2.Rectangle(blended, detection.BBox, new Scalar(0, 90, 220), thickness: 2);
        }
        return blended;
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private class LaneRuntime
    {
        public required string Name { get; init; }
        public required LaneState State { get; init; }
        public required ILaneInterlock Interlock { get; init; }
        public required IAreaScanCamera AreaScanCamera { get; init; }
        public required bool UseCorrelationResampling { get; init; }
        public required IForeignObjectDetector Detector { get; init; }
        public required double MaxPassSeconds { get; init; }
        public required int MaxFramesPerPass { get; init; }
        public required int ReferenceRowsPerMetre { get; init; }
        public required bool StorageEnabled { get; init; }
        public required string DriverImageDir { get; init; }
        public required string AnprImageDir { get; init; }
        public required string UnderVehicleImageDir { get; init; }
        public required IPlateDetector PlateDetector { get; init; }
        public required PlateCandidateSearch PlateSearch { get; init; }
        public required int TrackMaxFrames { get; init; }
        public required bool AggregationEnabled { get; init; }
        public required int AggregationWindowSize { get; init; }
        public required int AggregationMinCandidates { get; init; }
        public required IFaceDetector FaceDetector { get; init; }
        public required int FaceTrackMaxFrames { get; init; }
    }

    private class NullForeignObjectDetector : IForeignObjectDetector
    {
        public bool Available => false;
        public List<ForeignObjectDetection> Detect(Mat image) => new();
    }
}
