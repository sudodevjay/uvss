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
    private readonly IDbContextFactory<UvssDbContext> _dbFactory;
    private readonly ScanEventNotifier _scanEventNotifier;
    private readonly VehicleRegistryStore _vehicleRegistry;
    private readonly UvssService.Sensors.LaneInterlockRegistry _interlockRegistry;
    private readonly UvssService.Cameras.AreaScanCameraRegistry _areaScanCameraRegistry;

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
        if (_loopDefaults.ManualTriggerEnabled)
        {
            interlock = new ManualTriggerLaneInterlock(interlock);
        }
        // So the Lane Setup page's "Test Barrier Relay" button can reach
        // this exact running instance by lane name (see
        // LaneInterlockRegistry's own remarks) -- registered as soon as
        // it's built, before this lane's first pass ever runs.
        _interlockRegistry.Register(lane.Name, interlock);

        var areaScanCamera = AreaScanCameraFactory.Create(lane.BaslerCamera);
        // So Lane Setup's UVSS Camera Status column can read .Available by
        // lane name (see AreaScanCameraRegistry's own remarks).
        _areaScanCameraRegistry.Register(lane.Name, areaScanCamera);

        return new LaneRuntime
        {
            Name = lane.Name,
            State = _stateStore.GetOrCreate(lane.Name),
            Interlock = interlock,
            AreaScanCamera = areaScanCamera,
            Detector = detector,
            MaxPassSeconds = _loopDefaults.MaxPassSeconds,
            MaxFramesPerPass = _areaScan.MaxFramesPerPass,
            ReferenceRowsPerMetre = _areaScan.ReferenceRowsPerMetre,
            DebugPreviewDir = string.IsNullOrWhiteSpace(_areaScan.DebugPreviewDir) ? "" : ResolvePath(_areaScan.DebugPreviewDir),
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
            capture.Mosaic.Dispose();
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
            capture.Mosaic.Dispose();
        }
    }

    /// <summary>How often the live under-vehicle preview is pushed to the UI
    /// while a pass is in progress -- the whole point being that the image
    /// visibly builds up top-to-bottom as the vehicle drives over, the same
    /// way a real UVSS operator console works, instead of only appearing
    /// once the vehicle has already left (by which point it's too late for
    /// anyone watching to react to something suspicious).</summary>
    private static readonly TimeSpan LiveStitchUpdateInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Result of one capture pass: the under-vehicle mosaic built
    /// from every grabbed frame (see FrameMosaic), the ENGAGE trip time of
    /// every loop (index 0 = entry), and when the EXIT loop (the last one)
    /// actually cleared (went back to disengaged) -- capture keeps going past
    /// the exit loop engaging until it clears, so a vehicle longer than the
    /// loop spacing still gets its whole underside scanned.
    ///
    /// LoopTimes.Count tells you how far a pass got before a fault:
    /// still just [entryTime] means the exit loop never engaged at all;
    /// 2 entries with Completed==false means it engaged but never cleared
    /// (ExitClearTime stays null) -- two different fault stories, logged
    /// differently by RunOnePassAsync.</summary>
    private record CaptureResult(FrameMosaic Mosaic, List<DateTime> LoopTimes, DateTime? ExitClearTime, bool Completed);

    private static async Task<CaptureResult> CapturePassAsync(LaneRuntime lane, DateTime entryTime, CancellationToken ct)
    {
        lane.AreaScanCamera.Start();
        var mosaic = new FrameMosaic();
        var mosaicChanged = false;
        var loopTimes = new List<DateTime> { entryTime };
        var lastUiUpdate = DateTime.UtcNow;
        string? debugDir = null;
        if (lane.DebugPreviewDir != "")
        {
            debugDir = Path.Combine(lane.DebugPreviewDir, $"{lane.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(debugDir);
            Console.WriteLine($"[{lane.Name}] recording live previews to {debugDir}");
        }
        var debugIndex = 0;
        var totalLoops = lane.Interlock.LoopPositionsMetres.Count;
        var exitLoopIndex = totalLoops - 1;
        var currentLoopIndex = 0;
        // Once the exit loop ENGAGES, capture does NOT stop there -- a
        // vehicle longer than the entry-exit loop spacing still has part of
        // its underside to pass over the camera. Capture continues until the
        // exit loop goes back to disengaged (the vehicle's rear has cleared).
        var exitEngaged = false;
        DateTime? exitClearTime = null;
        var completed = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var elapsed = (DateTime.UtcNow - entryTime).TotalSeconds;
                if (elapsed >= lane.MaxPassSeconds || mosaic.FrameCount >= lane.MaxFramesPerPass)
                {
                    break;
                }

                using (var frame = lane.AreaScanCamera.GrabFrame(200))
                {
                    if (frame != null && mosaic.Add(frame))
                    {
                        mosaicChanged = true;
                    }
                }

                if (mosaicChanged && DateTime.UtcNow - lastUiUpdate >= LiveStitchUpdateInterval)
                {
                    PublishLiveMosaic(lane, mosaic, debugDir, ref debugIndex);
                    mosaicChanged = false;
                    lastUiUpdate = DateTime.UtcNow;
                }

                if (!exitEngaged)
                {
                    var nextLoopTime = await lane.Interlock.WaitForLoopAsync(currentLoopIndex + 1, loopTimes[currentLoopIndex], TimeSpan.Zero, ct);
                    if (nextLoopTime != null)
                    {
                        currentLoopIndex++;
                        loopTimes.Add(nextLoopTime.Value);
                        Console.WriteLine($"[{lane.Name}] loop {currentLoopIndex} engaged -- {mosaic.FrameCount} frames so far.");
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
                    Console.WriteLine($"[{lane.Name}] exit loop cleared -- {mosaic.FrameCount} frames total, vehicle fully passed.");
                    break;
                }
            }

            // Catch the live view up to the very last grabbed frame.
            if (mosaicChanged)
            {
                PublishLiveMosaic(lane, mosaic, debugDir, ref debugIndex);
            }
        }
        finally
        {
            lane.AreaScanCamera.Stop();
        }
        return new CaptureResult(mosaic, loopTimes, exitClearTime, completed);
    }

    /// <summary>The live view only shows the newest end of the scan (older
    /// rows scroll off the left), so only that much is re-encoded, at half
    /// resolution -- this runs on the grab loop and must stay cheap.</summary>
    private const int LivePreviewMaxRows = 4000;

    private static void PublishLiveMosaic(LaneRuntime lane, FrameMosaic mosaic, string? debugDir, ref int debugIndex)
    {
        using var live = mosaic.BuildRecent(LivePreviewMaxRows);
        if (live == null)
        {
            return;
        }
        // Explicit size, never below 1 row: a slow scene can leave a newest
        // strip only 1 row tall, which a plain 0.5 scale rounds to 0 rows
        // and OpenCV rejects ("!dsize.empty()"), killing the whole pass.
        using var half = new Mat();
        Cv2.Resize(live, half, new Size(Math.Max(1, live.Cols / 2), Math.Max(1, live.Rows / 2)), interpolation: InterpolationFlags.Area);
        var jpeg = EncodeUnderVehicleJpeg(half, mosaic.NewestAtTop);
        lane.State.SetUnderVehicleImage(jpeg);
        if (debugDir != null && jpeg != null)
        {
            File.WriteAllBytes(Path.Combine(debugDir, $"{debugIndex++:D4}_{mosaic.FrameCount}f_{mosaic.MosaicRows}r.jpg"), jpeg);
        }
    }

    /// <summary>The mosaic built during capture is already de-duplicated by
    /// image registration (see FrameMosaic) and keeps the camera's own
    /// pixel geometry, so it is used as the final image as-is -- the loops
    /// only contribute the average speed (loop spacing / entry-to-exit
    /// time).</summary>
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

        var finalImage = capture.Mosaic.Build();
        if (finalImage == null)
        {
            Console.WriteLine($"[{lane.Name}] no frames captured this pass -- skipping event.");
            return;
        }
        // The camera delivers Mono8; the detector and highlight drawing
        // expect BGR.
        if (finalImage.Channels() == 1)
        {
            var bgr = new Mat();
            Cv2.CvtColor(finalImage, bgr, ColorConversionCodes.GRAY2BGR);
            finalImage.Dispose();
            finalImage = bgr;
        }
        var newestAtTop = capture.Mosaic.NewestAtTop;

        var positions = lane.Interlock.LoopPositionsMetres;
        var exitTime = capture.LoopTimes[^1];
        var tailEndTime = capture.ExitClearTime ?? exitTime;
        var avgSpeed = LaneInterlockFactory.EstimateSpeedMetresPerSecond(positions[^1] - positions[0], capture.LoopTimes[0], exitTime);
        var totalDuration = (tailEndTime - capture.LoopTimes[0]).TotalSeconds;

        Console.WriteLine(
            $"[{lane.Name}] pass complete -- {capture.Mosaic.FrameCount} frames -> {finalImage.Cols}x{finalImage.Rows} mosaic, "
            + $"avg={avgSpeed:F2} m/s, duration={totalDuration:F2}s"
        );
        var m = capture.Mosaic;
        Console.WriteLine(
            $"[{lane.Name}] mosaic stats -- rejected: lowTexture={m.RejectedLowTexture} lowResponse={m.RejectedLowResponse} "
            + $"noMotion={m.RejectedNoMotion}; motion |dx|={m.TotalAbsDx:F0}px |dy|={m.TotalAbsDy:F0}px; kept {m.MosaicRows} rows"
        );

        // Reported downstream (LaneState.LastRowCount) as the total scan
        // ROWS grabbed across every frame -- a proxy for how much the camera
        // captured, independent of how much of it was new.
        var totalRowsCaptured = (int)Math.Min(int.MaxValue, capture.Mosaic.RowsGrabbed);

        var detections = lane.Detector.Available ? lane.Detector.Detect(finalImage) : new List<ForeignObjectDetection>();
        if (detections.Count > 0)
        {
            Console.WriteLine($"[{lane.Name}] foreign-object detector flagged {detections.Count} region(s).");
        }

        var underVehicleJpeg = EncodeUnderVehicleJpeg(finalImage, newestAtTop);
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
            highlightedUnderVehicleJpeg = EncodeUnderVehicleJpeg(highlighted, newestAtTop);
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
    /// looks like the image growing backwards. When the mosaic grew at the
    /// top instead (vehicle travelling the other way over the camera,
    /// FrameMosaic.NewestAtTop), rotating clockwise keeps that same rule:
    /// first-seen on the left, newest on the right.</summary>
    private static byte[]? EncodeUnderVehicleJpeg(Mat image, bool newestAtTop)
    {
        if (image.Empty())
        {
            return null;
        }
        using var rotated = new Mat();
        Cv2.Rotate(image, rotated, newestAtTop ? RotateFlags.Rotate90Clockwise : RotateFlags.Rotate90Counterclockwise);

        // Guard against oversized images -- past a certain size JPEG
        // encoding fails outright ("can't encode data: unknown exception").
        // A mosaic is normally well under this; only a very long, slow
        // pass reaches it.
        const int maxDimension = 8000;
        var largest = Math.Max(rotated.Cols, rotated.Rows);
        var toEncode = rotated;
        Mat? downscaled = null;
        if (largest > maxDimension)
        {
            var scale = maxDimension / (double)largest;
            downscaled = new Mat();
            Cv2.Resize(rotated, downscaled, new Size(Math.Max(1, (int)(rotated.Cols * scale)), Math.Max(1, (int)(rotated.Rows * scale))));
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
        public required IForeignObjectDetector Detector { get; init; }
        public required double MaxPassSeconds { get; init; }
        public required int MaxFramesPerPass { get; init; }
        public required int ReferenceRowsPerMetre { get; init; }
        public required string DebugPreviewDir { get; init; }
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
