using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Builds the under-vehicle image from a free-running AREA-scan
/// camera. Every grab is a whole 2D view (1920x1200), so stacking raw frames
/// just repeats the same scene over and over. Instead each new frame is
/// registered against the previous one (phase correlation, on a downscaled
/// copy for speed) to find how many rows the scene moved, and only that many
/// NEW rows are added -- the band that just crossed the frame's centre line. A stopped
/// vehicle adds nothing; a slow one adds a thin strip per frame; a fast one a
/// thick strip. Only the strips are kept, never the full frames, so memory
/// grows with the vehicle's length, not with pass duration.
///
/// Direction is taken from the image itself: content moving up means new
/// rows enter at the bottom (appended), moving down means they enter at the
/// top (prepended). Lateral (x) shift is ignored -- vehicles drive straight
/// over the camera.</summary>
public sealed class FrameMosaic : IDisposable
{
    /// <summary>Registration runs on a copy scaled to this fraction -- plenty
    /// of texture for phase correlation, ~16x cheaper than full resolution.</summary>
    private const double RegistrationScale = 0.25;

    /// <summary>Per-frame shifts below this (full-res pixels) count as "no
    /// motion" in the diagnostics only -- they are still accumulated, since
    /// each frame is measured against a fixed reference (see _refSmall).</summary>
    private const double MotionDeadbandPx = 0.5;

    /// <summary>Once the scene has moved this far (full-res rows) from the
    /// reference frame, the current frame becomes the new reference -- phase
    /// correlation gets unreliable as the overlap shrinks.</summary>
    private const double ReferenceResetRows = 100;

    /// <summary>Below this phase-correlation peak response the match is not
    /// trustworthy (featureless or blurred view) -- that frame adds nothing.
    /// Pure sensor noise in a dark scene still peaks around 0.05-0.1.</summary>
    private const double MinResponse = 0.15;

    /// <summary>A view this flat (grey-level std dev, e.g. lens covered or
    /// pointing into darkness) has nothing to register against -- any
    /// "shift" found is noise, so it adds nothing.</summary>
    private const double MinTextureStdDev = 3.0;

    /// <summary>Net movement (full-res rows) needed before the travel
    /// direction is locked. Hand jitter or vibration flips the sign of tiny
    /// shifts from frame to frame; deciding direction per frame made the
    /// image grow at both ends alternately (and the live view jump between
    /// them). A vehicle drives one way, so the first sustained movement
    /// decides it for the whole pass.</summary>
    private const double DirectionLockRows = 24;

    private readonly List<Mat> _top = new();    // prepended strips, in arrival order
    private readonly List<Mat> _bottom = new(); // appended strips, in arrival order
    private Mat? _first;
    /// <summary>Registration reference. Each frame is measured against this
    /// fixed frame, not the previous one: a slow scene moves well under a
    /// pixel per frame, and frame-to-frame those sub-pixel shifts were either
    /// thrown away as noise or summed up noise into drift. Against a fixed
    /// reference slow motion keeps adding up and a still scene stays put.</summary>
    private Mat? _refSmall;
    private Point2d _refShift; // last measured shift of the scene vs _refSmall (full-res px)
    private Mat? _window;
    private double _pendingRows;

    /// <summary>0 until locked; then -1 (scene moves up, rows appended at the
    /// bottom) or +1 (scene moves down, rows prepended at the top).</summary>
    private int _direction;

    public int FrameCount { get; private set; }

    // Per-pass diagnostics -- why frames did or didn't add rows.
    public int RejectedLowTexture { get; private set; }
    public int RejectedLowResponse { get; private set; }
    public int RejectedNoMotion { get; private set; }
    public double TotalAbsDx { get; private set; }
    public double TotalAbsDy { get; private set; }
    public int MosaicRows => _top.Sum(m => m.Rows) + _bottom.Sum(m => m.Rows);

    /// <summary>Total rows grabbed from the camera (all frames, before
    /// de-duplication).</summary>
    public long RowsGrabbed { get; private set; }

    /// <summary>True when the most recent growth was at the top -- the
    /// newest part of the image is then at row 0 rather than the last row.</summary>
    public bool NewestAtTop { get; private set; }

    /// <summary>Adds one grabbed frame. The caller keeps ownership of
    /// `frame` (this copies what it needs). Returns true if the mosaic grew.</summary>
    public bool Add(Mat frame)
    {
        if (frame.Empty())
        {
            return false;
        }
        FrameCount++;
        RowsGrabbed += frame.Rows;

        var small = ToRegistrationImage(frame);
        if (_first == null || _refSmall == null)
        {
            _first = frame.Clone();
            _refSmall = small;
            _refShift = default;
            _window = new Mat();
            Cv2.CreateHanningWindow(_window, small.Size(), MatType.CV_32F);
            return true;
        }

        Cv2.MeanStdDev(small, out _, out var stdDev);
        if (stdDev.Val0 < MinTextureStdDev)
        {
            RejectedLowTexture++;
            ReplaceReference(small);
            return false;
        }
        var raw = Cv2.PhaseCorrelate(_refSmall, small, _window!, out var response);
        if (response < MinResponse)
        {
            // Lost track of the reference (too far moved, or blurred) --
            // restart from this frame; the motion in between is lost.
            RejectedLowResponse++;
            ReplaceReference(small);
            return false;
        }
        var shift = new Point2d(raw.X / RegistrationScale, raw.Y / RegistrationScale);
        var dx = shift.X - _refShift.X;
        var dy = shift.Y - _refShift.Y;
        _refShift = shift;
        if (Math.Abs(shift.Y) >= ReferenceResetRows)
        {
            ReplaceReference(small);
        }
        else
        {
            small.Dispose();
        }
        TotalAbsDx += Math.Abs(dx);
        TotalAbsDy += Math.Abs(dy);
        if (Math.Abs(dy) < MotionDeadbandPx)
        {
            RejectedNoMotion++;
        }

        if (_direction == 0)
        {
            // Not locked yet: accumulate net (signed) movement until it is
            // clearly one way, then emit all of it in that direction.
            _pendingRows += dy;
            if (Math.Abs(_pendingRows) < DirectionLockRows)
            {
                return false;
            }
            _direction = Math.Sign(_pendingRows);
            _pendingRows = Math.Abs(_pendingRows);
        }
        else
        {
            // Locked: movement the other way builds up a debt, so backing
            // up over already-captured content adds nothing until the scene
            // is past the furthest point captured so far.
            _pendingRows += dy * _direction;
        }

        var rows = (int)Math.Truncate(_pendingRows);
        if (rows <= 0)
        {
            return false;
        }
        _pendingRows -= rows;
        rows *= _direction;

        // Moving faster than one frame height per grab leaves a gap -- the
        // best we can do is take the whole frame.
        // Strips are cut at the frame's CENTRE line, not its edge: the lens
        // vignettes hard towards the top/bottom rows (they come out black),
        // and the centre is the sharpest, best-lit part of the view. The
        // band that crossed the centre line since the last frame is exactly
        // the new content, so successive strips butt together with no gap
        // -- effectively a virtual line-scan camera at the centre row.
        var centre = frame.Rows / 2;
        var count = Math.Min(Math.Abs(rows), centre);
        if (rows < 0)
        {
            // Scene moved up: the new content just crossed the centre from below.
            _bottom.Add(frame.RowRange(centre - count, centre).Clone());
            NewestAtTop = false;
        }
        else
        {
            // Scene moved down: the new content just crossed the centre from above.
            _top.Add(frame.RowRange(centre, centre + count).Clone());
            NewestAtTop = true;
        }
        return true;
    }

    /// <summary>The mosaic so far as one new Mat (caller disposes), or null
    /// before the first frame. Until anything has moved it is just the first
    /// frame, so a pass where nothing moved still shows what the camera saw.</summary>
    public Mat? Build()
    {
        if (_first == null)
        {
            return null;
        }
        if (_top.Count == 0 && _bottom.Count == 0)
        {
            return _first.Clone();
        }
        var parts = new List<Mat>(_top.Count + _bottom.Count);
        for (var i = _top.Count - 1; i >= 0; i--)
        {
            parts.Add(_top[i]);
        }
        parts.AddRange(_bottom);
        var merged = new Mat();
        Cv2.VConcat(parts.ToArray(), merged);
        return merged;
    }

    /// <summary>Just the newest ~`maxRows` rows of the mosaic (caller
    /// disposes) -- for the live preview, which only ever shows the newest
    /// end anyway. Re-encoding the whole growing mosaic every update stalled
    /// the grab loop long enough for the camera's buffers to overflow.</summary>
    public Mat? BuildRecent(int maxRows)
    {
        if (_first == null)
        {
            return null;
        }
        var newest = NewestAtTop ? _top : _bottom;
        if (newest.Count == 0)
        {
            return _first.Clone();
        }
        var parts = new List<Mat>();
        var rows = 0;
        for (var i = newest.Count - 1; i >= 0 && rows < maxRows; i--)
        {
            parts.Add(newest[i]);
            rows += newest[i].Rows;
        }
        // parts is newest-first; put it back in image order.
        if (!NewestAtTop)
        {
            parts.Reverse();
        }
        var merged = new Mat();
        Cv2.VConcat(parts.ToArray(), merged);
        return merged;
    }

    private void ReplaceReference(Mat small)
    {
        _refSmall?.Dispose();
        _refSmall = small;
        _refShift = default;
    }

    private static Mat ToRegistrationImage(Mat frame)
    {
        using var resized = new Mat();
        if (frame.Channels() == 1)
        {
            Cv2.Resize(frame, resized, new Size(), RegistrationScale, RegistrationScale, InterpolationFlags.Area);
        }
        else
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Resize(gray, resized, new Size(), RegistrationScale, RegistrationScale, InterpolationFlags.Area);
        }
        var result = new Mat();
        resized.ConvertTo(result, MatType.CV_32F);
        return result;
    }

    public void Dispose()
    {
        _first?.Dispose();
        _refSmall?.Dispose();
        _window?.Dispose();
        foreach (var strip in _top.Concat(_bottom))
        {
            strip.Dispose();
        }
        _top.Clear();
        _bottom.Clear();
    }
}
