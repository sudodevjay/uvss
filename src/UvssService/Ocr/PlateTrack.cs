using OpenCvSharp;
using UvssService.Detection;

namespace UvssService.Ocr;

/// <summary>Exact C# port of anpr-ai-service/main.py's per-vehicle plate
/// track (add_best_track_frame/selected_track_frames/plate_frame_visibility_score,
/// main.py:2273-2311) -- collects ANPR frames sampled during one vehicle
/// pass, scores each cheaply (plate-detector only, no OCR -- OCR is far
/// more expensive and only worth running on the frames actually worth
/// reading), and keeps only the best-scoring PlateTrackMaxFrames (40)
/// throughout the pass, from which the best AggregationWindowSize (10) are
/// selected for full OCR once the pass ends.</summary>
public class PlateTrack
{
    private record Entry(Mat Frame, double Score, int Sequence);

    private readonly List<Entry> _entries = new();
    private readonly IPlateDetector _plateDetector;
    private readonly int _maxFrames;
    private int _sequence;

    public PlateTrack(IPlateDetector plateDetector, int maxFrames)
    {
        _plateDetector = plateDetector;
        _maxFrames = maxFrames;
    }

    public int FrameCount => _entries.Count;

    /// <summary>Cheap (no-OCR) visibility score: how promising this frame
    /// looks for a plate read, based only on detection quality/aspect/edge
    /// clearance -- mirrors plate_frame_visibility_score exactly.</summary>
    public double VisibilityScore(Mat frame)
    {
        if (!_plateDetector.Available) return 0.0;
        var boxes = _plateDetector.Detect(frame);
        if (boxes.Count == 0) return 0.0;

        var height = frame.Rows;
        var width = frame.Cols;
        var bestScore = 0.0;
        foreach (var box in boxes.Take(3))
        {
            var clamped = ClampRect(box.BBox, width, height);
            using var crop = new Mat(frame, clamped);
            var quality = PlateImageVariants.PlateCropQualityScore(crop, box.Confidence);
            var x1 = clamped.X;
            var y1 = clamped.Y;
            var x2 = clamped.X + clamped.Width;
            var y2 = clamped.Y + clamped.Height;
            var edgeClearance = new[] { x1, y1, width - x2, height - y2 }.Min();
            var edgeScore = edgeClearance <= 2 ? 0.65 : 1.0;
            bestScore = Math.Max(bestScore, quality * edgeScore);
        }
        return bestScore;
    }

    public void AddFrame(Mat frame)
    {
        _sequence++;
        var score = VisibilityScore(frame);
        _entries.Add(new Entry(frame.Clone(), score, _sequence));
        // Keep only the best plate-visible frames. When no plate is
        // detected yet (score=0), newer frames win so the fallback still
        // follows the vehicle -- same tie-break as the Python original.
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

    public List<Mat> SelectedFrames(int windowSize)
    {
        return _entries
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.Sequence)
            .Take(windowSize)
            .Select(e => e.Frame)
            .ToList();
    }

    public void Reset()
    {
        foreach (var e in _entries) e.Frame.Dispose();
        _entries.Clear();
        _sequence = 0;
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
