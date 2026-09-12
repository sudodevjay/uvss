using OpenCvSharp;

namespace UvssService.Detection;

/// <summary>Per-vehicle driver-photo track -- same shape as Ocr.PlateTrack
/// (collect frames sampled during one pass, score each cheaply, keep only
/// the best), except there's no text to decode/vote on afterwards: the
/// "aggregation" step is just "return the single highest-scoring frame".
///
/// Score favors a bigger, sharper, more-centered face over a smaller/
/// blurrier/off-to-the-side one -- these weights are a reasonable first-cut
/// heuristic (not tuned against a labeled dataset, since none exists for
/// this yet), same honesty caveat as the foreign-object detector's
/// synthetic training data.</summary>
public class DriverTrack
{
    private record Entry(Mat Frame, double Score, int Sequence);

    private readonly List<Entry> _entries = new();
    private readonly IFaceDetector _faceDetector;
    private readonly int _maxFrames;
    private int _sequence;

    public DriverTrack(IFaceDetector faceDetector, int maxFrames)
    {
        _faceDetector = faceDetector;
        _maxFrames = maxFrames;
    }

    public int FrameCount => _entries.Count;

    public double VisibilityScore(Mat frame)
    {
        if (!_faceDetector.Available) return 0.0;
        var faces = _faceDetector.Detect(frame);
        if (faces.Count == 0) return 0.0;

        var width = frame.Cols;
        var height = frame.Rows;
        var bestScore = 0.0;
        foreach (var face in faces)
        {
            var clamped = ClampRect(face, width, height);
            using var crop = new Mat(frame, clamped);
            var sharpness = Sharpness(crop);

            var sizeScore = Math.Min(clamped.Width * clamped.Height / 20000.0, 1.0);
            var sharpnessScore = Math.Min(sharpness / 150.0, 1.0);

            var faceCx = clamped.X + clamped.Width / 2.0;
            var faceCy = clamped.Y + clamped.Height / 2.0;
            var dx = Math.Abs(faceCx - width / 2.0) / (width / 2.0);
            var dy = Math.Abs(faceCy - height / 2.0) / (height / 2.0);
            var centerScore = Math.Max(0.0, 1.0 - (dx + dy) / 2.0);

            var score = sizeScore * 0.4 + sharpnessScore * 0.4 + centerScore * 0.2;
            bestScore = Math.Max(bestScore, score);
        }
        return bestScore;
    }

    public void AddFrame(Mat frame)
    {
        _sequence++;
        var score = VisibilityScore(frame);
        _entries.Add(new Entry(frame.Clone(), score, _sequence));
        // Same tie-break as PlateTrack: when no face has been seen yet
        // (score=0), keep the newest frames so the fallback still follows
        // the vehicle instead of getting stuck on stale early frames.
        _entries.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : b.Sequence.CompareTo(a.Sequence);
        });
        while (_entries.Count > _maxFrames)
        {
            _entries[^1].Frame.Dispose();
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    /// <summary>The single best frame collected this pass, or null if
    /// nothing was ever added (e.g. the driver camera was unavailable).</summary>
    public Mat? BestFrame()
    {
        return _entries.OrderByDescending(e => e.Score).ThenByDescending(e => e.Sequence).FirstOrDefault()?.Frame;
    }

    public void Reset()
    {
        foreach (var e in _entries) e.Frame.Dispose();
        _entries.Clear();
        _sequence = 0;
    }

    private static double Sharpness(Mat image)
    {
        if (image.Empty()) return 0.0;
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        return stddev.Val0 * stddev.Val0;
    }

    private static Rect ClampRect(Rect box, int width, int height)
    {
        var x1 = Math.Max(0, box.X);
        var y1 = Math.Max(0, box.Y);
        var x2 = Math.Min(width, box.X + box.Width);
        var y2 = Math.Min(height, box.Y + box.Height);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }
}
