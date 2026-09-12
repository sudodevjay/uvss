using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Area-scan analogue of SpeedSweepTest -- same speed-variation
/// scenarios, same physical setup and CorrelationResampler under test, but
/// the raw capture is now built the way an AREA-scan camera actually
/// produces it: one full FrameHeightPx-tall snapshot of the camera's fixed
/// field of view every 1/FramesPerSecond seconds, NOT a near-continuous
/// stream of 2-row slivers the way a real line-scan sensor (and
/// SpeedSweepTest's own model of one) does.
///
/// This distinction matters for two separate reasons:
///
/// 1. Coverage: a line-scan sensor's native line rate (thousands/sec) is
///    always far faster than any realistic vehicle speed needs, so it's
///    ALWAYS oversampling -- gaps are a non-issue in practice. An area-scan
///    camera's frame rate is 1-2 orders of magnitude slower, so whether
///    consecutive frames still overlap (vs. leave a gap of genuinely
///    uncaptured ground CorrelationResampler cannot invent back) depends on
///    FramesPerSecond * FrameHeightRealWorldLength actually exceeding the
///    fastest vehicle this lane will ever see. This is a hardware sizing
///    question that must be checked against the real camera's spec, not a
///    software fix.
/// 2. Duplicate SHAPE: within the oversampled regime, line-scan duplication
///    grows in single-row increments; area-scan duplication grows in whole
///    FrameHeightPx-row blocks per tick. CorrelationResampler's tuned
///    defaults (chunkHeight=2, templateHeight=1, minConfidence=0.95) were
///    only ever validated against the former (see SpeedSweepTest/
///    CorrelationResamplerSelfTest, both built from 2-row ticks) -- this
///    harness is what actually checks whether those same defaults still
///    hold up against the latter, rather than assuming they generalize.
///
/// Run via `dotnet run -- --test-area-scan-speed-sweep`.</summary>
public static class AreaScanSpeedSweepTest
{
    private const double PassDistanceM = 3.0;
    private const double TargetRowsPerMetre = 500;

    public static void Run()
    {
        // Matches this project's current BaslerCameraOptions defaults
        // (SimulatedFramesPerSecond / SimulatedFrameHeightPx) -- placeholders
        // until the real camera's configured AcquisitionFrameRate and actual
        // mounted field-of-view (working distance + lens FOV) are known.
        // Re-run this with the real numbers once confirmed.
        var configs = new (int FramesPerSecond, int FrameHeightPx)[]
        {
            (25, 64),
            (25, 200),
            (60, 64),
            (100, 64),
        };

        var trueHeight = (int)(PassDistanceM * TargetRowsPerMetre);
        var reference = BuildTexturedReferenceImage(trueHeight, 300);

        var scenarios = new (string Name, (double Seconds, double FromKmh, double ToKmh)[] Profile)[]
        {
            ("Constant 10 km/h", new[] { (5.0, 10.0, 10.0) }),
            ("Constant 20 km/h", new[] { (5.0, 20.0, 20.0) }),
            ("Constant 30 km/h", new[] { (5.0, 30.0, 30.0) }),
            ("Ramp up 10 -> 30 km/h", new[] { (0.5, 10.0, 10.0), (1.0, 10.0, 30.0), (5.0, 30.0, 30.0) }),
            ("Hard brake mid-pass 30 -> 5 -> 20 km/h", new[] { (0.3, 30.0, 30.0), (0.1, 30.0, 5.0), (0.3, 5.0, 5.0), (0.1, 5.0, 20.0), (5.0, 20.0, 20.0) }),
            ("Oscillating 30/10/30/10 km/h", new[] { (0.25, 30.0, 30.0), (0.1, 30.0, 10.0), (0.25, 10.0, 10.0), (0.1, 10.0, 30.0), (0.25, 30.0, 30.0), (0.1, 30.0, 10.0), (5.0, 10.0, 10.0) }),
            ("Full stop mid-pass 20 -> 0 -> 20 km/h", new[] { (0.3, 20.0, 20.0), (0.1, 20.0, 0.0), (0.5, 0.0, 0.0), (0.1, 0.0, 20.0), (5.0, 20.0, 20.0) }),
            ("Gradual deceleration 30 -> 10 km/h", new[] { (2.0, 30.0, 10.0), (5.0, 10.0, 10.0) }),
            ("Sudden late brake 25 -> 25 -> 5 km/h", new[] { (0.6, 25.0, 25.0), (0.05, 25.0, 5.0), (5.0, 5.0, 5.0) }),
        };

        foreach (var (fps, frameHeightPx) in configs)
        {
            // Max vehicle speed this (fps, frameHeightPx) combo can capture
            // with zero gaps: the camera must advance no more than
            // frameHeightPx rows'-worth of real ground between frames.
            var maxGapFreeSpeedMps = frameHeightPx / TargetRowsPerMetre * fps;
            var maxGapFreeKmh = maxGapFreeSpeedMps * 3.6;

            Console.WriteLine($"=== {fps} fps, {frameHeightPx}px-tall frames ({fps * frameHeightPx} rows/sec throughput) ===");
            Console.WriteLine($"Gap-free up to {maxGapFreeKmh:F1} km/h (frameHeightPx/TargetRowsPerMetre * fps) -- faster than that, ground is skipped and unrecoverable by any resampling.");
            Console.WriteLine($"{"Scenario",-42} {"Raw error",10} {"Corrected error",16} {"Gaps?",8}");
            Console.WriteLine(new string('-', 82));

            var worst = 0.0;
            foreach (var (name, profile) in scenarios)
            {
                var raw = BuildRawAreaScanCapture(reference, profile, trueHeight, fps, frameHeightPx, out var trueEndRow, out var hadGap);
                var rawErrorPct = Math.Abs(raw.Rows - trueEndRow) / (double)trueEndRow * 100;

                using var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw);
                var correctedErrorPct = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow * 100;
                worst = Math.Max(worst, correctedErrorPct);

                Console.WriteLine($"{name,-42} {rawErrorPct,8:F1}% {correctedErrorPct,14:F1}% {(hadGap ? "YES" : ""),8}");
                raw.Dispose();
            }
            Console.WriteLine();
            Console.WriteLine($"Worst case corrected error across all scenarios: {worst:F1}%");
            Console.WriteLine();
        }
    }

    /// <summary>Diagnostic-only parameter sweep: is CorrelationResampler's
    /// approach fundamentally sound for area-scan's coarser, frame-block
    /// duplicate shape (just needs different chunkHeight/templateHeight/
    /// minConfidence than the line-scan-tuned defaults), or does the
    /// technique break down entirely at this granularity? Restricted to
    /// scenarios that are gap-free at the given (fps, frameHeightPx) --
    /// gap scenarios are unfixable by definition and would only add noise
    /// to this comparison. Run via `dotnet run -- --test-area-scan-tune`.</summary>
    public static void RunTune()
    {
        var trueHeight = (int)(PassDistanceM * TargetRowsPerMetre);
        var reference = BuildTexturedReferenceImage(trueHeight, 300);

        var lowSpeedScenarios = new (string Name, (double Seconds, double FromKmh, double ToKmh)[] Profile)[]
        {
            ("Constant 5 km/h", new[] { (5.0, 5.0, 5.0) }),
            ("Constant 10 km/h", new[] { (5.0, 10.0, 10.0) }),
            ("Ramp 5 -> 15 km/h", new[] { (0.5, 5.0, 5.0), (1.0, 5.0, 15.0), (5.0, 15.0, 15.0) }),
            ("Full stop mid-pass 10 -> 0 -> 10 km/h", new[] { (0.3, 10.0, 10.0), (0.1, 10.0, 0.0), (0.5, 0.0, 0.0), (0.1, 0.0, 10.0), (5.0, 10.0, 10.0) }),
        };

        foreach (var (fps, frameHeightPx) in new[] { (25, 200), (60, 64) })
        {
            var paramSets = new (int ChunkHeight, int TemplateHeight, double MinConfidence)[]
            {
                (2, 1, 0.95),      // current line-scan-tuned production default, for comparison
                (frameHeightPx / 4, frameHeightPx / 8, 0.90),
                (frameHeightPx / 2, frameHeightPx / 4, 0.90),
                (frameHeightPx, frameHeightPx / 2, 0.85),
                (frameHeightPx * 2, frameHeightPx, 0.85),
            };

            Console.WriteLine($"=== {fps} fps, {frameHeightPx}px-tall frames (low-speed / gap-free scenarios only) ===");
            foreach (var (chunkHeight, templateHeight, minConfidence) in paramSets)
            {
                Console.WriteLine($"-- chunkHeight={chunkHeight}, templateHeight={templateHeight}, minConfidence={minConfidence} --");
                var worst = 0.0;
                foreach (var (name, profile) in lowSpeedScenarios)
                {
                    var raw = BuildRawAreaScanCapture(reference, profile, trueHeight, fps, frameHeightPx, out var trueEndRow, out var hadGap);
                    if (hadGap)
                    {
                        Console.WriteLine($"  {name,-40} SKIPPED (unexpected gap)");
                        raw.Dispose();
                        continue;
                    }
                    using var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw, chunkHeight, templateHeight, minConfidence);
                    var correctedErrorPct = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow * 100;
                    worst = Math.Max(worst, correctedErrorPct);
                    Console.WriteLine($"  {name,-40} corrected error {correctedErrorPct,6:F1}%");
                    raw.Dispose();
                }
                Console.WriteLine($"  worst case: {worst:F1}%");
            }
            Console.WriteLine();
        }
    }

    /// <summary>Final confirmation sweep: chunkHeight/templateHeight locked
    /// to the actual per-grab frame height (the one setting that mattered,
    /// per RunTune's finding) -- vary only minConfidence, across the FULL
    /// original speed-variation scenario set (constant/ramp/brake/
    /// oscillate/stop), same rigor as the original line-scan (2,1,0.95)
    /// tuning. Run via `dotnet run -- --test-area-scan-tune-confidence`.</summary>
    public static void RunTuneConfidence()
    {
        var trueHeight = (int)(PassDistanceM * TargetRowsPerMetre);
        var reference = BuildTexturedReferenceImage(trueHeight, 300);

        var scenarios = new (string Name, (double Seconds, double FromKmh, double ToKmh)[] Profile)[]
        {
            ("Constant 5 km/h", new[] { (5.0, 5.0, 5.0) }),
            ("Constant 10 km/h", new[] { (5.0, 10.0, 10.0) }),
            ("Constant 15 km/h", new[] { (5.0, 15.0, 15.0) }),
            ("Constant 20 km/h", new[] { (5.0, 20.0, 20.0) }),
            ("Ramp up 5 -> 20 km/h", new[] { (0.5, 5.0, 5.0), (1.0, 5.0, 20.0), (5.0, 20.0, 20.0) }),
            ("Hard brake mid-pass 20 -> 3 -> 15 km/h", new[] { (0.3, 20.0, 20.0), (0.1, 20.0, 3.0), (0.3, 3.0, 3.0), (0.1, 3.0, 15.0), (5.0, 15.0, 15.0) }),
            ("Oscillating 20/5/20/5 km/h", new[] { (0.25, 20.0, 20.0), (0.1, 20.0, 5.0), (0.25, 5.0, 5.0), (0.1, 5.0, 20.0), (0.25, 20.0, 20.0), (0.1, 20.0, 5.0), (5.0, 5.0, 5.0) }),
            ("Full stop mid-pass 15 -> 0 -> 15 km/h", new[] { (0.3, 15.0, 15.0), (0.1, 15.0, 0.0), (0.5, 0.0, 0.0), (0.1, 0.0, 15.0), (5.0, 15.0, 15.0) }),
            ("Gradual deceleration 20 -> 5 km/h", new[] { (2.0, 20.0, 5.0), (5.0, 5.0, 5.0) }),
            ("Sudden late brake 18 -> 18 -> 3 km/h", new[] { (0.6, 18.0, 18.0), (0.05, 18.0, 3.0), (5.0, 3.0, 3.0) }),
        };

        foreach (var (fps, frameHeightPx) in new[] { (25, 200), (60, 64) })
        {
            var maxGapFreeKmh = frameHeightPx / TargetRowsPerMetre * fps * 3.6;
            var chunkHeight = frameHeightPx;
            Console.WriteLine($"=== {fps} fps, {frameHeightPx}px frames -- chunkHeight={chunkHeight} (gap-free up to {maxGapFreeKmh:F1} km/h) ===");

            foreach (var templateFraction in new[] { 2, 4, 6, 8, 12, 16 })
            {
                var templateHeight = Math.Max(1, frameHeightPx / templateFraction);
                foreach (var minConfidence in new[] { 0.95, 0.85, 0.70 })
                {
                    var worst = 0.0;
                    var worstName = "";
                    var skipped = 0;
                    foreach (var (name, profile) in scenarios)
                    {
                        var raw = BuildRawAreaScanCapture(reference, profile, trueHeight, fps, frameHeightPx, out var trueEndRow, out var hadGap);
                        if (hadGap) { skipped++; raw.Dispose(); continue; }
                        using var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw, chunkHeight, templateHeight, minConfidence);
                        var errPct = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow * 100;
                        if (errPct > worst) { worst = errPct; worstName = name; }
                        raw.Dispose();
                    }
                    Console.WriteLine($"  templateHeight=frameHeightPx/{templateFraction}({templateHeight})  minConfidence={minConfidence:F2}  worst case={worst,6:F1}% [{worstName}]  ({scenarios.Length - skipped}/{scenarios.Length} gap-free)");
                }
            }
            Console.WriteLine();
        }
    }

    /// <summary>Same sweep as Run(), but against a REAL photo instead of the
    /// synthetic circles-and-lines texture -- real camera footage has noise,
    /// natural gradients, and flatter/less-distinctive stretches that are
    /// harder for image correlation to match than a designed-to-correlate-
    /// well synthetic pattern, so this is the more trustworthy check of how
    /// the resampler actually behaves. Uses the CURRENT production
    /// simulated-camera config (25fps/64px) plus the validated chunk-size
    /// fix (chunkHeight=frameHeightPx, templateHeight=frameHeightPx/4,
    /// minConfidence=0.85 -- see ResampleAreaScanFrames in
    /// LaneWorkerHostedService, which this mirrors exactly), across:
    /// a fine constant-speed sweep (to find exactly where things degrade)
    /// plus the specific compound scenarios asked for -- slow, fast,
    /// slow-brake-fast, fast-brake-slow, oscillating, full stop.
    /// Run via `dotnet run -- --test-area-scan-real &lt;imagePath&gt;`.</summary>
    public static void RunRealImage(string imagePath, int fps = 25, int frameHeightPx = 64)
    {
        var chunkHeight = frameHeightPx;
        var templateHeight = Math.Max(1, frameHeightPx / 4);
        const double minConfidence = 0.85;

        var trueHeight = (int)(PassDistanceM * TargetRowsPerMetre);
        var maxGapFreeKmh = frameHeightPx / TargetRowsPerMetre * fps * 3.6;

        var reference = LoadRealReferenceImage(imagePath, trueHeight + frameHeightPx + 50);

        Console.WriteLine($"=== Real image: {imagePath} ===");
        Console.WriteLine($"Config: {fps} fps, {frameHeightPx}px frames, chunkHeight={chunkHeight}, templateHeight={templateHeight}, minConfidence={minConfidence}");
        Console.WriteLine($"Hard gap-free ceiling: {maxGapFreeKmh:F1} km/h -- above this, ground is physically skipped, no resampling can recover it.");
        Console.WriteLine();

        Console.WriteLine("-- Fine constant-speed sweep --");
        Console.WriteLine($"{"Speed",10} {"Raw error",10} {"Corrected error",16} {"Gaps?",8}");
        foreach (var kmh in new[] { 2.0, 5.0, 8.0, 10.0, 12.0, 15.0, 18.0, 20.0, 22.0, 25.0, 28.0, 30.0, 32.0, 35.0, 40.0, 50.0 })
        {
            var profile = new[] { (5.0, kmh, kmh) };
            RunOneScenario($"Constant {kmh:F0} km/h", profile, reference, trueHeight, fps, frameHeightPx, chunkHeight, templateHeight, minConfidence);
        }

        Console.WriteLine();
        Console.WriteLine("-- Requested compound scenarios (targeting up to 30 km/h) --");
        var compoundScenarios = new (string Name, (double Seconds, double FromKmh, double ToKmh)[] Profile)[]
        {
            ("Slow constant (10 km/h)", new[] { (5.0, 10.0, 10.0) }),
            ("Medium constant (20 km/h)", new[] { (5.0, 20.0, 20.0) }),
            ("Fast constant (30 km/h)", new[] { (5.0, 30.0, 30.0) }),
            ("Slow -> brake -> fast (10 -> 2 -> 30 km/h)", new[] { (1.0, 10.0, 10.0), (0.5, 10.0, 2.0), (0.5, 2.0, 2.0), (0.3, 2.0, 30.0), (5.0, 30.0, 30.0) }),
            ("Fast -> brake -> slow (30 -> 3 -> 10 km/h)", new[] { (1.0, 30.0, 30.0), (0.3, 30.0, 3.0), (0.5, 3.0, 3.0), (0.5, 3.0, 10.0), (5.0, 10.0, 10.0) }),
            ("Ramp up 10 -> 30 km/h", new[] { (0.5, 10.0, 10.0), (1.0, 10.0, 30.0), (5.0, 30.0, 30.0) }),
            ("Oscillating 30/10/30/10 km/h", new[] { (0.25, 30.0, 30.0), (0.1, 30.0, 10.0), (0.25, 10.0, 10.0), (0.1, 10.0, 30.0), (0.25, 30.0, 30.0), (0.1, 30.0, 10.0), (5.0, 10.0, 10.0) }),
            ("Full stop mid-pass (25 -> 0 -> 25 km/h)", new[] { (0.3, 25.0, 25.0), (0.1, 25.0, 0.0), (0.5, 0.0, 0.0), (0.1, 0.0, 25.0), (5.0, 25.0, 25.0) }),
            ("Sudden late brake (28 -> 28 -> 5 km/h)", new[] { (0.6, 28.0, 28.0), (0.05, 28.0, 5.0), (5.0, 5.0, 5.0) }),
        };
        foreach (var (name, profile) in compoundScenarios)
        {
            RunOneScenario(name, profile, reference, trueHeight, fps, frameHeightPx, chunkHeight, templateHeight, minConfidence);
        }

        Console.WriteLine();
        Console.WriteLine($"CONCLUSION: gap-free (no missing ground) only up to {maxGapFreeKmh:F1} km/h at {fps}fps/{frameHeightPx}px. See the fine sweep above for where corrected accuracy actually starts degrading within that ceiling.");
    }

    private static void RunOneScenario(
        string name, (double Seconds, double FromKmh, double ToKmh)[] profile, Mat reference, int trueHeight,
        int fps, int frameHeightPx, int chunkHeight, int templateHeight, double minConfidence)
    {
        var raw = BuildRawAreaScanCapture(reference, profile, trueHeight, fps, frameHeightPx, out var trueEndRow, out var hadGap);
        var rawErrorPct = Math.Abs(raw.Rows - trueEndRow) / (double)trueEndRow * 100;
        using var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw, chunkHeight, templateHeight, minConfidence);
        var correctedErrorPct = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow * 100;
        Console.WriteLine($"{name,-42} {rawErrorPct,8:F1}% {correctedErrorPct,14:F1}% {(hadGap ? "YES" : ""),8}");
        raw.Dispose();
    }

    /// <summary>Loads a real photo as the sweep's reference image, resized
    /// tall enough to cover the full 3m simulated pass -- keeps the source's
    /// own texture/noise (unlike BuildTexturedReferenceImage's synthetic
    /// pattern), which is what makes this sweep a more trustworthy check of
    /// real-world correlation behaviour.</summary>
    private static Mat LoadRealReferenceImage(string imagePath, int minHeight)
    {
        var loaded = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (loaded.Empty())
        {
            throw new InvalidOperationException($"Could not load reference image: {imagePath}");
        }
        if (loaded.Rows >= minHeight)
        {
            return loaded;
        }
        var scale = minHeight / (double)loaded.Rows;
        var resized = new Mat();
        Cv2.Resize(loaded, resized, new Size((int)(loaded.Cols * scale), minHeight));
        loaded.Dispose();
        return resized;
    }

    /// <summary>Quick param search against a real image, restricted to
    /// speeds up to 30 km/h specifically (what RunRealImage found needs
    /// tightening) -- sweeps frameHeightPx/fps combos (same gap-free
    /// ceiling, different overlap distribution) x template fraction x
    /// minConfidence, reporting worst-case error across 10/20/30 km/h
    /// constant plus the brake/ramp/oscillate/stop scenarios. Run via
    /// `dotnet run -- --test-area-scan-real-tune &lt;imagePath&gt;`.</summary>
    public static void RunRealImageTune(string imagePath)
    {
        var trueHeight = (int)(PassDistanceM * TargetRowsPerMetre);
        var reference = LoadRealReferenceImage(imagePath, trueHeight + 300);

        var scenarios = new (string Name, (double Seconds, double FromKmh, double ToKmh)[] Profile)[]
        {
            ("Constant 10 km/h", new[] { (5.0, 10.0, 10.0) }),
            ("Constant 20 km/h", new[] { (5.0, 20.0, 20.0) }),
            ("Constant 30 km/h", new[] { (5.0, 30.0, 30.0) }),
            ("Slow -> brake -> fast (10->2->30)", new[] { (1.0, 10.0, 10.0), (0.5, 10.0, 2.0), (0.5, 2.0, 2.0), (0.3, 2.0, 30.0), (5.0, 30.0, 30.0) }),
            ("Fast -> brake -> slow (30->3->10)", new[] { (1.0, 30.0, 30.0), (0.3, 30.0, 3.0), (0.5, 3.0, 3.0), (0.5, 3.0, 10.0), (5.0, 10.0, 10.0) }),
            ("Ramp up 10 -> 30", new[] { (0.5, 10.0, 10.0), (1.0, 10.0, 30.0), (5.0, 30.0, 30.0) }),
            ("Oscillating 30/10", new[] { (0.25, 30.0, 30.0), (0.1, 30.0, 10.0), (0.25, 10.0, 10.0), (0.1, 10.0, 30.0), (5.0, 10.0, 10.0) }),
            ("Full stop 25->0->25", new[] { (0.3, 25.0, 25.0), (0.1, 25.0, 0.0), (0.5, 0.0, 0.0), (0.1, 0.0, 25.0), (5.0, 25.0, 25.0) }),
        };

        // All give a gap-free ceiling comfortably above 30 km/h (at least
        // ~2.5x margin) -- what varies is the overlap distribution shape.
        var configs = new (int Fps, int FrameHeightPx)[]
        {
            (100, 150), (150, 100), (200, 75), (100, 100), (150, 150), (250, 60),
        };

        foreach (var (fps, frameHeightPx) in configs)
        {
            var ceiling = frameHeightPx / TargetRowsPerMetre * fps * 3.6;
            Console.WriteLine($"=== {fps}fps/{frameHeightPx}px (ceiling {ceiling:F0} km/h) ===");
            foreach (var templateFraction in new[] { 3, 4, 6, 8 })
            {
                var templateHeight = Math.Max(1, frameHeightPx / templateFraction);
                foreach (var minConfidence in new[] { 0.95, 0.90, 0.85, 0.75 })
                {
                    var worst = 0.0;
                    var worstName = "";
                    foreach (var (name, profile) in scenarios)
                    {
                        var raw = BuildRawAreaScanCapture(reference, profile, trueHeight, fps, frameHeightPx, out var trueEndRow, out var hadGap);
                        using var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw, frameHeightPx, templateHeight, minConfidence);
                        var errPct = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow * 100;
                        if (errPct > worst) { worst = errPct; worstName = name + (hadGap ? " [GAP]" : ""); }
                        raw.Dispose();
                    }
                    Console.WriteLine($"  templateHeight=/{templateFraction}({templateHeight}) minConf={minConfidence:F2}  worst={worst,6:F1}% [{worstName}]");
                }
            }
        }
    }

    private static Mat BuildTexturedReferenceImage(int height, int width)
    {
        var image = new Mat(height, width, MatType.CV_8UC3, new Scalar(90, 92, 95));
        var rng = new Random(42);
        for (var i = 0; i < height / 2; i++)
        {
            var y = rng.Next(0, height);
            var x = rng.Next(0, width);
            var r = rng.Next(4, 18);
            var shade = rng.Next(10, 230);
            Cv2.Circle(image, new Point(x, y), r, new Scalar(shade, shade, shade), -1);
        }
        for (var y = 0; y < height; y += rng.Next(25, 55))
        {
            Cv2.Line(image, new Point(0, y), new Point(width, y), new Scalar(40, 40, 40), rng.Next(1, 4));
        }
        return image;
    }

    /// <summary>Builds a raw capture the way BaslerAreaScanCamera/
    /// SimulatedAreaScanCamera actually would: one FrameHeightPx-tall
    /// snapshot of the reference image's current position every
    /// 1/FramesPerSecond seconds, position advancing between frames by
    /// however far the (possibly varying) speed profile says the vehicle
    /// actually moved in that interval. If position advances by MORE than
    /// FrameHeightPx between two frames, that gap of skipped rows is real,
    /// permanent data loss -- flagged via hadGap, not silently absorbed.</summary>
    private static Mat BuildRawAreaScanCapture(
        Mat reference, (double Seconds, double FromKmh, double ToKmh)[] profile, int trueHeight,
        int framesPerSecond, int frameHeightPx, out int trueEndRow, out bool hadGap)
    {
        var frames = new List<Mat>();
        double positionRows = 0;
        var dt = 1.0 / framesPerSecond;
        var reachedEnd = false;
        hadGap = false;

        foreach (var (segSeconds, fromKmh, toKmh) in profile)
        {
            var ticks = (int)Math.Round(segSeconds * framesPerSecond);
            for (var t = 0; t < ticks; t++)
            {
                var frac = ticks <= 1 ? 1.0 : t / (double)(ticks - 1);
                var kmh = fromKmh + (toKmh - fromKmh) * frac;
                var mps = kmh / 3.6;
                var advanceRows = mps * TargetRowsPerMetre * dt;
                if (advanceRows > frameHeightPx)
                {
                    hadGap = true;
                }
                positionRows += advanceRows;

                var row = (int)Math.Round(positionRows);
                if (row + frameHeightPx >= trueHeight)
                {
                    reachedEnd = true;
                    break;
                }
                frames.Add(reference[row, row + frameHeightPx, 0, reference.Cols].Clone());
            }
            if (reachedEnd) break;
        }

        trueEndRow = (int)Math.Round(positionRows);
        var stitched = new Mat();
        Cv2.VConcat(frames.ToArray(), stitched);
        foreach (var f in frames) f.Dispose();
        return stitched;
    }
}
