using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Validation harness for CorrelationResampler covering constant
/// and variable vehicle speeds under a REALISTIC physical setup (3m loop
/// spacing, 500 lines/m calibration target, a 5000 lines/sec free-running
/// camera -- no encoder), unlike CorrelationResamplerSelfTest's abstract
/// tick-based profiles. This is what found the real bug that led to
/// CorrelationResampler's current (2, 1, 0.95) defaults -- see that
/// method's own remarks for the root cause -- and is what should be re-run
/// if those defaults, or the assumed camera rate/calibration target, ever
/// change.
///
/// Run via `dotnet run -- --test-speed-sweep`.</summary>
public static class SpeedSweepTest
{
    private const double PassDistanceM = 3.0;
    private const double CameraLinesPerSecond = 5000;
    private const double TargetLinesPerMetre = 500;

    public static void Run()
    {
        var trueHeight = (int)(PassDistanceM * TargetLinesPerMetre);
        var reference = BuildTexturedReferenceImage(trueHeight, 300);

        // Each segment: hold or ramp from FromKmh to ToKmh over Seconds. The
        // last segment in every scenario is deliberately long -- capture
        // stops naturally the instant the simulated vehicle reaches the
        // real 3m distance (mirroring how CapturePassAsync itself stops on
        // the exit loop), so segment durations don't need to be hand-tuned
        // to sum to exactly 3m.
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

        Console.WriteLine($"Setup: {PassDistanceM}m pass, camera={CameraLinesPerSecond} lines/sec (free-running, no encoder), target={TargetLinesPerMetre} lines/m calibration.");
        Console.WriteLine();
        Console.WriteLine($"{"Scenario",-42} {"Raw error",10} {"Corrected error",16}");
        Console.WriteLine(new string('-', 72));

        var worst = 0.0;
        foreach (var (name, profile) in scenarios)
        {
            var raw = BuildRawCapture(reference, profile, trueHeight, out var trueEndRow);
            var rawErrorPct = Math.Abs(raw.Rows - trueEndRow) / (double)trueEndRow * 100;

            using var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw);
            var correctedErrorPct = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow * 100;
            worst = Math.Max(worst, correctedErrorPct);

            Console.WriteLine($"{name,-42} {rawErrorPct,8:F1}% {correctedErrorPct,14:F1}%");
            raw.Dispose();
        }
        Console.WriteLine();
        Console.WriteLine($"Worst case corrected error across all scenarios: {worst:F1}%");
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

    private static Mat BuildRawCapture(Mat reference, (double Seconds, double FromKmh, double ToKmh)[] profile, int trueHeight, out int trueEndRow)
    {
        var lines = new List<Mat>();
        double positionRows = 0;
        var dt = 1.0 / CameraLinesPerSecond;
        var reachedEnd = false;

        foreach (var (segSeconds, fromKmh, toKmh) in profile)
        {
            var ticks = (int)Math.Round(segSeconds * CameraLinesPerSecond);
            for (var t = 0; t < ticks; t++)
            {
                var frac = ticks <= 1 ? 1.0 : t / (double)(ticks - 1);
                var kmh = fromKmh + (toKmh - fromKmh) * frac;
                var mps = kmh / 3.6;
                positionRows += mps * TargetLinesPerMetre * dt;

                var row = (int)Math.Round(positionRows);
                if (row + 2 >= trueHeight)
                {
                    reachedEnd = true;
                    break;
                }
                lines.Add(reference[row, row + 2, 0, reference.Cols].Clone());
            }
            if (reachedEnd) break;
        }

        trueEndRow = (int)Math.Round(positionRows);
        var stitched = new Mat();
        Cv2.VConcat(lines.ToArray(), stitched);
        foreach (var l in lines) l.Dispose();
        return stitched;
    }
}
