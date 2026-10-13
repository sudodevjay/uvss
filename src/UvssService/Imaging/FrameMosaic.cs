using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Builds the under-vehicle image from a free-running AREA-scan
/// camera. Every grab is a whole 2D view, so stacking raw frames just
/// repeats the same scene over and over. Instead the motion of the scene
/// between grabs is measured (DisMotionEstimator -- dense optical flow,
/// which follows only the part of the view that moves) and only that many
/// NEW rows are added -- the band that just crossed the frame's centre line.
/// A stopped vehicle adds nothing; a slow one adds a thin strip per frame; a
/// fast one a thick strip. Only the strips are kept, never the full frames,
/// so memory grows with the vehicle's length, not with pass duration.
///
/// Motion is measured along the frame's rows: content moving up means new
/// rows enter at the bottom (appended), moving down means they enter at the
/// top (prepended). Lateral (x) shift is ignored -- vehicles drive straight
/// over the camera. A lane whose vehicles cross the image sideways passes
/// transposed frames in.</summary>
public sealed class FrameMosaic : IDisposable
{
    /// <summary>Per-frame shifts below this (full-res pixels) count as "no
    /// motion" in the diagnostics only.</summary>
    private const double MotionDeadbandPx = 0.5;

    /// <summary>Most backward motion (rows) remembered as debt -- enough to
    /// cancel noise on a still scene, small enough that a bad reading can't
    /// stall the image.</summary>
    private const double MaxBackwardDebtRows = 48;

    /// <summary>Net movement (full-res rows) needed before the travel
    /// direction is locked. Hand jitter or vibration flips the sign of tiny
    /// shifts from frame to frame; deciding direction per frame made the
    /// image grow at both ends alternately (and the live view jump between
    /// them). A vehicle drives one way, so the first sustained movement
    /// decides it for the whole pass.</summary>
    private const double DirectionLockRows = 24;

    private readonly List<Mat> _top = new();    // prepended strips, in arrival order
    private readonly List<Mat> _bottom = new(); // appended strips, in arrival order
    private readonly DisMotionEstimator _motion = new();
    private Mat? _first;
    private double _pendingRows;

    /// <summary>0 until locked; then -1 (scene moves up, rows appended at the
    /// bottom) or +1 (scene moves down, rows prepended at the top).</summary>
    private int _direction;

    /// <summary>`direction`: 0 = decide from the first sustained movement
    /// (see DirectionLockRows); -1 = the scene moves up the frame, +1 = down.
    /// A lane's direction of travel is fixed by the installation, and
    /// auto-locking was fooled by whatever moved first (a hand, a head).</summary>
    public FrameMosaic(int direction = 0)
    {
        _direction = Math.Sign(direction);
    }

    public int FrameCount { get; private set; }

    // Per-pass diagnostics -- why frames did or didn't add rows.
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

        var motion = _motion.Next(frame);
        if (_first == null)
        {
            _first = frame.Clone();
            return true;
        }
        var (dx, dy) = motion ?? (0, 0);
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
            // Locked: movement the other way builds up a debt, so jitter
            // back and forth cancels out instead of growing the image. The
            // debt is capped: one bad reading (a large bogus backward shift)
            // used to swallow all forward motion after it, freezing the
            // image -- live view included -- for the rest of the pass.
            _pendingRows = Math.Max(_pendingRows + dy * _direction, -MaxBackwardDebtRows);
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
    /// the grab loop long enough for the camera's buffers to overflow.
    /// Null until something has moved: showing the whole first frame and
    /// then swapping it for the first thin strip looked like a restart.</summary>
    public Mat? BuildRecent(int maxRows)
    {
        var newest = NewestAtTop ? _top : _bottom;
        if (newest.Count == 0)
        {
            return null;
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

    public void Dispose()
    {
        _first?.Dispose();
        _motion.Dispose();
        foreach (var strip in _top.Concat(_bottom))
        {
            strip.Dispose();
        }
        _top.Clear();
        _bottom.Clear();
    }
}
