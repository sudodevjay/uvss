using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Validation harness for CorrelationResampler -- not part of the
/// production pipeline. Builds a textured "ground truth" under-vehicle
/// image, simulates a raw capture distorted by a varying speed profile
/// (fast / slow / a full stop, mid-pass), runs the correlation resampler on
/// it, and compares the result's row count back against the known ground
/// truth so the fix can be judged by a number, not a guess.
///
/// Validated results with the production defaults (chunkHeight=2,
/// templateHeight=1, minConfidence=0.95 -- changed from the original
/// (10, 14, 0.35), then from an intermediate (2, 1, 0.8), see
/// CorrelationResampler.ResampleBySelfCorrelation's own remarks for why),
/// across two different speed profiles:
///   Profile A (fast/near-stop/fast/slow): raw capture 79% off the true
///   distance -> corrected to 12% off.
///   Profile B (slow/fast/near-stop/medium/medium): raw capture 73% off
///   -> corrected to 13% off.
/// (The old (10, 14) defaults scored much better on exactly these two
/// profiles -- 1% and 7% off -- but that parameter combination has a
/// serious flaw for a DIFFERENT, more realistic scenario: a vehicle holding
/// one constant, heavily-oversampled speed for a whole pass, where it loses
/// 71-86% of the true distance. The (2, 1) shape is the best available
/// trade-off across both kinds of scenario, not a strictly better result on
/// this specific pair of profiles -- see SpeedSweepTest, which covers the
/// constant/variable-speed side of this and was used to find that shape.
/// minConfidence=0.95 is a further refinement on top of that shape: raising
/// it from 0.8 rejects more of the borderline/spurious matches that were
/// hurting these two profiles specifically, with zero effect on
/// SpeedSweepTest's scenarios -- see CorrelationResampler's own remarks for
/// the full sweep.)
///
/// Run via `dotnet run -- --test-correlation`.</summary>
public static class CorrelationResamplerSelfTest
{
    private const int Width = 300;
    private const int TrueHeight = 1400;

    public static void Run()
    {
        var reference = BuildTexturedReferenceImage(TrueHeight, Width);

        // Every rowsPerTick value stays under the 2px-tall per-tick capture
        // height, i.e. the camera is always oversampling (as a real system
        // would be designed to be across its whole rated speed range) --
        // this technique corrects *within* that regime, it doesn't lift the
        // regime's ceiling (see CorrelationResampler's remarks).
        var profileA = new (int ticks, double rowsPerTick)[]
        {
            (200, 1.6), (150, 0.1), (200, 1.8), (250, 0.8),
        };
        var profileB = new (int ticks, double rowsPerTick)[]
        {
            (100, 0.3), (300, 1.9), (100, 0.05), (150, 1.2), (200, 1.0),
        };

        RunOneProfile("Profile A (fast/near-stop/fast/slow)", reference, profileA, "test_A");
        RunOneProfile("Profile B (slow/fast/near-stop/medium/medium)", reference, profileB, "test_B");
    }

    private static void RunOneProfile(string label, Mat reference, (int ticks, double rowsPerTick)[] profile, string fileTag)
    {
        Console.WriteLine($"=== {label} ===");
        var raw = BuildDistortedRawCapture(reference, profile, out var trueEndRow);
        Cv2.ImWrite($"{fileTag}_raw.jpg", raw);

        var rawError = Math.Abs(raw.Rows - trueEndRow) / (double)trueEndRow;
        Console.WriteLine($"True distance travelled:  {trueEndRow} rows");
        Console.WriteLine($"Raw capture (uncorrected): {raw.Rows} rows ({rawError:P0} off target)");

        var corrected = CorrelationResampler.ResampleBySelfCorrelation(raw);
        Cv2.ImWrite($"{fileTag}_corrected.jpg", corrected);
        var correctedError = Math.Abs(corrected.Rows - trueEndRow) / (double)trueEndRow;
        Console.WriteLine($"Correlation-resampled:     {corrected.Rows} rows ({correctedError:P0} off target)");
        Console.WriteLine();
    }

    private static Mat BuildTexturedReferenceImage(int height, int width)
    {
        var image = new Mat(height, width, MatType.CV_8UC3, new Scalar(90, 92, 95));
        var rng = new Random(42);
        // Dense scattering of blobs (~1 per 2 rows) so every few-row window
        // has *some* distinguishing feature to correlate against -- sparse
        // texture with long flat stretches is exactly what real correlation
        // (and this test) struggles with.
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

    private static Mat BuildDistortedRawCapture(Mat reference, (int ticks, double rowsPerTick)[] speedSegments, out int trueEndRow)
    {
        var lines = new List<Mat>();
        double position = 0;
        foreach (var (ticks, rowsPerTick) in speedSegments)
        {
            for (var t = 0; t < ticks; t++)
            {
                position += rowsPerTick;
                var row = (int)Math.Round(position);
                if (row + 2 >= reference.Rows)
                {
                    break;
                }
                lines.Add(reference[row, row + 2, 0, reference.Cols].Clone());
            }
        }
        trueEndRow = (int)Math.Round(position);
        var stitched = new Mat();
        Cv2.VConcat(lines.ToArray(), stitched);
        return stitched;
    }
}
