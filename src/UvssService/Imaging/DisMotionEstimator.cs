using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using OpenCvSharp;
using EmguMat = Emgu.CV.Mat;
using Mat = OpenCvSharp.Mat;

namespace UvssService.Imaging;

/// <summary>How far the MOVING part of the scene shifted between two
/// consecutive grabs, from dense DIS optical flow (Emgu.CV -- OpenCvSharp
/// has no DIS).
///
/// A single global registration (phase correlation) found the dominant
/// shift of the whole frame, so whenever the vehicle covered only part of
/// the view -- entering, leaving, or narrower than the view -- the static
/// background won and the pass "stopped". Dense flow gives a shift per
/// pixel: pixels that did not move are ignored, and the vehicle's motion is
/// the peak of what the rest report (see DominantMotion).
/// DIS also propagates flow into plain areas from the textured edges around
/// them, which phase correlation and feature trackers cannot.
///
/// Consecutive frames, not a fixed keyframe: measured against a keyframe,
/// plain surfaces drifted out of match and whole passes collapsed to a few
/// rows. Sensor noise on a still scene makes small back-and-forth readings;
/// FrameMosaic's direction lock turns backward motion into debt, so they
/// cancel out instead of growing the image (450 still frames -> 4 rows).</summary>
public sealed class DisMotionEstimator : IDisposable
{
    /// <summary>Flow runs on a copy scaled to this fraction -- plenty of
    /// detail for a vehicle underside, and keeps one flow call well inside a
    /// 30 fps frame interval (~7-15 ms on the full 1936x1216 frame).</summary>
    public const double Scale = 0.25;

    /// <summary>Pixels whose flow along the travel axis is below this
    /// (scaled px) count as not moving -- the static background sits here.</summary>
    private const float MoveThresholdPx = 0.15f;

    /// <summary>Fewer moving pixels than this fraction of the view = nothing
    /// is moving (a few noisy pixels must not drive the image).</summary>
    private const double MinMovingFraction = 0.03;

    /// <summary>Half-width (scaled px) of the window around the flow
    /// histogram's peak that counts as "the vehicle's motion"...</summary>
    private const float PeakHalfWidthPx = 0.25f;

    /// <summary>...or this fraction of the shift itself, whichever is wider.</summary>
    private const double PeakRelativeWidth = 0.2;

    /// <summary>The motion peak must hold at least this many times the
    /// pixels of the same-size window at the opposite shift.</summary>
    private const double MinPeakOverMirror = 2.0;

    // Flow histogram over [-HistRange, HistRange) scaled px.
    private const float HistRange = 64f;
    private const float HistBin = 0.02f;

    /// <summary>Emgu's OpenCV runs DIS on every core by default. During a
    /// pass that took the whole laptop CPU (~95%), and the camera's packets
    /// then arrived late enough for whole runs of frames to come in
    /// "incompletely grabbed". Two threads keep up with 30 fps.</summary>
    private const int FlowThreads = 2;

    static DisMotionEstimator()
    {
        CvInvoke.NumThreads = FlowThreads;
    }

    private readonly DISOpticalFlow _dis = new(DISOpticalFlow.Preset.Medium);
    private readonly int[] _hist = new int[(int)(2 * HistRange / HistBin)];
    private readonly int[] _cumulative = new int[(int)(2 * HistRange / HistBin) + 1];
    private Mat? _previous;
    private float[] _flow = Array.Empty<float>();

    /// <summary>Movement of the scene (full-resolution px) since the
    /// previous call, or null when nothing measurable moved. `frame` is
    /// Mono8 or BGR and stays owned by the caller.</summary>
    public (double Dx, double Dy)? Next(Mat frame)
    {
        var small = ToSmallGray(frame);
        var previous = _previous;
        _previous = small;
        if (previous == null)
        {
            return null;
        }
        using (previous)
        {
            return Measure(previous, small);
        }
    }

    private (double Dx, double Dy)? Measure(Mat previous, Mat current)
    {
        using var a = WrapForEmgu(previous);
        using var b = WrapForEmgu(current);
        using var flow = new EmguMat();
        _dis.Calc(a, b, flow);

        var n = current.Rows * current.Cols;
        if (_flow.Length != 2 * n)
        {
            _flow = new float[2 * n];
        }
        Marshal.Copy(flow.DataPointer, _flow, 0, 2 * n);

        var motion = DominantMotion(n);
        if (motion == null)
        {
            return null;
        }
        var (dx, dy) = motion.Value;
        return (dx / Scale, dy / Scale);
    }

    /// <summary>The vehicle's shift = the most common travel-axis flow
    /// among the moving pixels (histogram peak), not their median: while a
    /// vehicle only covers part of the view, noise in the static background
    /// makes many small random readings that dragged the median to ~0 --
    /// entering vehicles "did not move" and fast passes came out short. The
    /// vehicle's own pixels all agree, so they form one sharp peak. Null when
    /// that peak is too small to be anything but noise.</summary>
    private (double Dx, double Dy)? DominantMotion(int n)
    {
        Array.Clear(_hist);
        for (var i = 0; i < n; i++)
        {
            var vy = _flow[2 * i + 1];
            if (Math.Abs(vy) < MoveThresholdPx)
            {
                continue;
            }
            var bin = (int)((vy + HistRange) / HistBin);
            if ((uint)bin < (uint)_hist.Length)
            {
                _hist[bin]++;
            }
        }
        // Peak of the histogram smoothed over a window that widens with the
        // shift: the wide-angle lens makes the middle of the vehicle move
        // faster than its edges, so a fast vehicle's readings spread over
        // +-20% -- a fixed narrow window missed it and frames "did not move".
        var cumulative = _cumulative;
        cumulative[0] = 0;
        for (var b = 0; b < _hist.Length; b++)
        {
            cumulative[b + 1] = cumulative[b] + _hist[b];
        }
        int WindowCount(int centreBin, out int lo, out int hi)
        {
            var v = Math.Abs((centreBin + 0.5) * HistBin - HistRange);
            var half = (int)(Math.Max(PeakHalfWidthPx, PeakRelativeWidth * v) / HistBin);
            lo = Math.Max(0, centreBin - half);
            hi = Math.Min(_hist.Length, centreBin + half + 1);
            return cumulative[hi] - cumulative[lo];
        }
        int best = -1, bestCount = 0;
        for (var b = 0; b < _hist.Length; b++)
        {
            var c = WindowCount(b, out _, out _);
            if (c > bestCount)
            {
                bestCount = c;
                best = b;
            }
        }
        if (best < 0 || bestCount < MinMovingFraction * n)
        {
            return null;
        }
        // Noise is symmetric: on a still scene the readings just past the
        // threshold pile up equally at +v and -v, and the larger of the two
        // was reported as motion (~1.7 px/frame of creep). A real vehicle
        // moves one way, so its peak must clearly beat its mirror image.
        var mirror = WindowCount(_hist.Length - 1 - best, out _, out _);
        if (bestCount < MinPeakOverMirror * mirror)
        {
            return null;
        }
        // Mean flow of the pixels inside the peak window, both axes.
        WindowCount(best, out var binLo, out var binHi);
        var vLo = binLo * HistBin - HistRange;
        var vHi = binHi * HistBin - HistRange;
        double sumX = 0, sumY = 0;
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            var vy = _flow[2 * i + 1];
            if (Math.Abs(vy) < MoveThresholdPx || vy < vLo || vy >= vHi)
            {
                continue;
            }
            sumX += _flow[2 * i];
            sumY += vy;
            count++;
        }
        return count == 0 ? null : (sumX / count, sumY / count);
    }

    private static Mat ToSmallGray(Mat frame)
    {
        var small = new Mat();
        if (frame.Channels() == 1)
        {
            Cv2.Resize(frame, small, new OpenCvSharp.Size(), Scale, Scale, InterpolationFlags.Area);
        }
        else
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Resize(gray, small, new OpenCvSharp.Size(), Scale, Scale, InterpolationFlags.Area);
        }
        return small;
    }

    /// <summary>An Emgu header over the same pixels -- no copy.</summary>
    private static EmguMat WrapForEmgu(Mat m) =>
        new(m.Rows, m.Cols, DepthType.Cv8U, 1, m.Data, (int)m.Step());

    public void Dispose()
    {
        _previous?.Dispose();
        _dis.Dispose();
    }
}
