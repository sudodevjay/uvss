using OpenCvSharp;

namespace UvssService.Cameras;

/// <summary>Local dev/testing stand-in for AxisIpCamera -- instead of hitting
/// a real Axis camera's HTTP snapshot endpoint, either cycles through every
/// image in TestImageDir (a folder of still .jpg photos) or, if TestImageDir
/// names a video file directly, plays that video back live and continuously.
/// Lets a simulated UVSS pass exercise the full plate-detect/OCR/
/// multi-frame-aggregation pipeline with real vehicle footage, without
/// needing real camera hardware.</summary>
public class SimulatedIpCamera : IIpCamera
{
    private readonly string[] _imagePaths;
    private readonly SimulatedVehicleSlot _slot;
    private readonly VideoCapture? _video;
    private readonly double _videoFps;
    private readonly int _videoFrameCount;
    private readonly object _videoLock = new();
    private DateTime _lastAdvanceUtc;
    private Mat? _lastFrame;

    /// <summary>Never advance more than this many frames in one
    /// GetSnapshotAsync call, even if a lot of wall-clock time elapsed
    /// since the last call (e.g. right after startup, or the poll loop
    /// stalled briefly) -- bounds the worst-case decode burst instead of
    /// letting a long gap force reading hundreds of frames at once.</summary>
    private const int MaxFramesPerAdvance = 90;

    public SimulatedIpCamera(string testImageDir, SimulatedVehicleSlot slot)
    {
        _slot = slot;
        if (VideoFileTestSource.IsVideoFile(testImageDir))
        {
            _video = new VideoCapture(testImageDir);
            _videoFps = _video.Fps > 0 ? _video.Fps : 30;
            _videoFrameCount = (int)_video.FrameCount;
            _imagePaths = Array.Empty<string>();
            _lastAdvanceUtc = DateTime.UtcNow;
            if (_videoFrameCount <= 0)
            {
                Console.WriteLine($"SimulatedIpCamera: could not open video '{testImageDir}' -- snapshots will be unavailable.");
            }
            return;
        }

        _imagePaths = Directory.Exists(testImageDir)
            ? Directory.GetFiles(testImageDir, "*.jpg").OrderBy(p => p).ToArray()
            : Array.Empty<string>();
        if (_imagePaths.Length == 0)
        {
            Console.WriteLine($"SimulatedIpCamera: no .jpg files found in '{testImageDir}' -- snapshots will be unavailable.");
        }
    }

    public Task<Mat?> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (_video != null)
        {
            return Task.FromResult(GetVideoFrame());
        }
        if (_imagePaths.Length == 0)
        {
            return Task.FromResult<Mat?>(null);
        }
        var index = _slot.Current % _imagePaths.Length;
        var frame = Cv2.ImRead(_imagePaths[index], ImreadModes.Color);
        return Task.FromResult<Mat?>(frame.Empty() ? null : frame);
    }

    /// <summary>Advances the video by however many frames wall-clock time
    /// says should have elapsed since the last call, using ONLY sequential
    /// Read() calls -- deliberately NOT seeking to an absolute frame number
    /// on every call. OpenCV/FFmpeg's CAP_PROP_POS_FRAMES seek is decoded
    /// against the nearest preceding keyframe and is unreliable for
    /// arbitrary positions on a typical H.264 file with sparse keyframes --
    /// in practice this kept landing on (close to) the same frame every
    /// call, which is why every simulated pass was reading the same one
    /// vehicle regardless of how much real time had actually passed. It was
    /// also the CPU cost behind the dashboard stuttering: a fresh keyframe
    /// decode on every single poll (150ms, x3 cameras, x2 lanes) versus one
    /// cheap sequential decode advancing a handful of frames. Looping back
    /// to the start (frame 0) is the one seek this still does, and that one
    /// is safe -- frame 0 is always a keyframe.</summary>
    private Mat? GetVideoFrame()
    {
        if (_video == null || _videoFrameCount <= 0)
        {
            return null;
        }
        lock (_videoLock)
        {
            var now = DateTime.UtcNow;
            var framesToAdvance = (int)((now - _lastAdvanceUtc).TotalSeconds * _videoFps);
            if (framesToAdvance > 0 || _lastFrame == null)
            {
                framesToAdvance = Math.Clamp(framesToAdvance, 1, MaxFramesPerAdvance);
                _lastAdvanceUtc = now;
                for (var i = 0; i < framesToAdvance; i++)
                {
                    try
                    {
                        var frame = new Mat();
                        if (!_video.Read(frame) || frame.Empty())
                        {
                            // Reached the end -- loop back to the start.
                            _video.Set(VideoCaptureProperties.PosFrames, 0);
                            if (!_video.Read(frame) || frame.Empty())
                            {
                                frame.Dispose();
                                break;
                            }
                        }
                        // Only swap in the new frame once it's confirmed
                        // good -- an occasional bad decode (rare but seen
                        // in practice with sparse-keyframe H.264 right
                        // around a loop-back) must not leave _lastFrame
                        // disposed-but-still-referenced, which would corrupt
                        // every call after it.
                        var previous = _lastFrame;
                        _lastFrame = frame;
                        previous?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"SimulatedIpCamera: skipping one bad video frame: {ex.Message}");
                        break;
                    }
                }
            }
            // A clone: _lastFrame is reused/replaced across calls, but
            // callers (PlateTrack/DriverTrack) may hold onto a returned
            // frame well past this call returning.
            return _lastFrame?.Clone();
        }
    }
}
