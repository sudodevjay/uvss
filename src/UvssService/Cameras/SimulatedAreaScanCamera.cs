using OpenCvSharp;

namespace UvssService.Cameras;

/// <summary>Simulated area-scan camera for local dev/testing without Basler
/// hardware attached. TestImagePath can point at:
///   - a video file (.mp4/.avi/.mov/.mkv) -- each GrabFrame() call reads the
///     NEXT sequential frame from that footage (looping back to the start
///     once exhausted), the most direct real-camera analogue: one frame
///     grab in, one video frame out.
///   - a folder of .jpg photos -- slices the pass's chosen photo into
///     successive FrameHeightPx-tall strips (the original demo behaviour).
///   - a single .jpg -- same slicing, always from that one photo.
///
/// Paces itself at `framesPerSecond` rather than handing back all frames
/// instantly: a real area-scan camera is rate-limited by its own frame rate,
/// and without *some* pacing here every frame is available within
/// microseconds of Start(), which starves the live-preview throttle in
/// LaneWorkerHostedService of any chance to publish more than one
/// (already-complete) update per pass -- exactly the
/// collect-everything-then-show-it-all-at-once behavior this whole feature
/// exists to avoid.</summary>
public class SimulatedAreaScanCamera : IAreaScanCamera
{
    // Same SimulatedVehicleSlot (advanced only at pass entry, not on a
    // timer -- see its own remarks) as SimulatedIpCamera, so when
    // TestImagePath names a directory, the chassis image picked for a new
    // pass is guaranteed to match whichever "vehicle" the driver/ANPR/
    // overview cameras show for that same pass, no matter how long the
    // pass runs. Not used in video mode -- see class remarks.
    private readonly string _singleImagePath;
    private readonly string[] _imagePaths;
    private readonly int _frameWidthPx;
    private readonly int _frameHeightPx;
    private readonly int _framesPerSecond;
    private readonly SimulatedVehicleSlot _slot;
    private Mat _image;
    private int _row;

    private readonly VideoCapture? _video;
    private int _videoFrameCount;

    public SimulatedAreaScanCamera(
        string testImagePath, int frameWidthPx, int frameHeightPx, SimulatedVehicleSlot slot, int framesPerSecond = 25)
    {
        _singleImagePath = testImagePath;
        _frameWidthPx = frameWidthPx;
        _frameHeightPx = Math.Max(frameHeightPx, 1);
        _slot = slot;
        _framesPerSecond = Math.Max(framesPerSecond, 1);

        if (VideoFileTestSource.IsVideoFile(testImagePath))
        {
            _video = new VideoCapture(testImagePath);
            _videoFrameCount = (int)_video.FrameCount;
            _imagePaths = Array.Empty<string>();
            _image = new Mat();
            if (_videoFrameCount <= 0)
            {
                Console.WriteLine($"SimulatedAreaScanCamera: could not open video '{testImagePath}' -- frames will be unavailable.");
            }
            return;
        }

        _imagePaths = Directory.Exists(testImagePath)
            ? Directory.GetFiles(testImagePath, "*.jpg").OrderBy(p => p).ToArray()
            : Array.Empty<string>();
        _image = LoadCurrentImage();
    }

    public bool Available => true;

    public void Start()
    {
        if (_video != null)
        {
            // Deliberately NOT reset to frame 0 -- letting the sequential
            // position keep advancing pass after pass (wrapping at the
            // video's end) is what gives each new pass genuinely different
            // captured footage, the video-mode analogue of
            // SimulatedVehicleSlot picking a new photo per pass.
            return;
        }
        _row = 0;
        if (_imagePaths.Length > 0)
        {
            // Pick fresh at the start of each pass (not per-frame) so the
            // whole pass stitches from one consistent image.
            _image.Dispose();
            _image = LoadCurrentImage();
        }
    }

    private Mat LoadCurrentImage()
    {
        if (_imagePaths.Length > 0)
        {
            var index = _slot.Current % _imagePaths.Length;
            var loaded = Cv2.ImRead(_imagePaths[index]);
            if (!loaded.Empty())
            {
                return loaded;
            }
        }
        else if (!string.IsNullOrEmpty(_singleImagePath) && File.Exists(_singleImagePath))
        {
            var loaded = Cv2.ImRead(_singleImagePath);
            if (!loaded.Empty())
            {
                return loaded;
            }
        }
        return BuildSyntheticTestImage(_frameWidthPx);
    }

    public Mat? GrabFrame(int timeoutMs = 200)
    {
        if (_video != null)
        {
            return GrabVideoFrame();
        }

        if (_row >= _image.Rows)
        {
            return null;
        }
        Thread.Sleep(1000 / _framesPerSecond);
        var height = Math.Min(_frameHeightPx, _image.Rows - _row);
        // .Clone() is required, not cosmetic: this camera instance is shared
        // across every pass for this lane's whole lifetime (one _image
        // buffer, reused pass after pass), and FinalizePassAsync now runs in
        // the background (see LaneWorkerHostedService) so a previous pass's
        // finalize can still be reading its captured frames while the NEXT
        // pass is already grabbing new ones. A bare submat view here aliases
        // _image's own buffer/refcount across both passes -- returning an
        // owned copy instead makes each grabbed frame fully independent, the
        // same way a real camera SDK hands back its own frame buffer rather
        // than a view into shared internal state.
        var frame = _image[_row, _row + height, 0, _image.Cols].Clone();
        _row += height;
        return frame;
    }

    /// <summary>Sequential (not wall-clock-synced) video read: each call
    /// advances one frame further into the footage, looping back to the
    /// start once exhausted -- the direct area-scan analogue of a real
    /// camera's own free-running frame grab, one call in, one frame out.</summary>
    private Mat? GrabVideoFrame()
    {
        if (_video == null || _videoFrameCount <= 0)
        {
            return null;
        }
        Thread.Sleep(1000 / _framesPerSecond);
        lock (_video)
        {
            var frame = new Mat();
            if (!_video.Read(frame) || frame.Empty())
            {
                // Reached the end -- loop back to the start and retry once.
                _video.Set(VideoCaptureProperties.PosFrames, 0);
                if (!_video.Read(frame) || frame.Empty())
                {
                    return null;
                }
            }
            return frame;
        }
    }

    public void Stop() { }

    private static Mat BuildSyntheticTestImage(int width)
    {
        const int height = 600;
        var image = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);
        var barColors = new[]
        {
            new Scalar(60, 60, 220), new Scalar(60, 200, 220), new Scalar(60, 220, 90),
            new Scalar(220, 200, 60), new Scalar(220, 90, 60),
        };
        var barWidth = width / barColors.Length;
        for (var i = 0; i < barColors.Length; i++)
        {
            var rect = new Rect(i * barWidth, 0, barWidth, height);
            Cv2.Rectangle(image, rect, barColors[i], -1);
        }
        for (var y = 0; y < height; y += 40)
        {
            Cv2.Line(image, new Point(0, y), new Point(width, y), Scalar.White, 1);
        }
        return image;
    }
}
