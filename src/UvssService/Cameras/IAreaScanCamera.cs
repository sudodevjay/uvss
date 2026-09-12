using OpenCvSharp;

namespace UvssService.Cameras;

/// <summary>The under-vehicle area-scan camera: grabs one full 2D frame at a
/// time while the vehicle drives over it, for FrameStitcher to assemble.
/// Each returned Mat is a whole frame (arbitrary width x height), not a
/// single pixel row -- successive frames are stacked/de-duplicated the same
/// way successive lines would be from a true line-scan sensor.</summary>
public interface IAreaScanCamera
{
    bool Available { get; }
    void Start();
    Mat? GrabFrame(int timeoutMs = 200);
    void Stop();
}
