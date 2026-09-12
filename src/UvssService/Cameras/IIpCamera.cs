using OpenCvSharp;

namespace UvssService.Cameras;

public class CameraUnavailableException : Exception
{
    public CameraUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Driver camera / ANPR camera -- a plain IP camera polled for a
/// single snapshot at a time (the lane worker polls this repeatedly while a
/// lane is Active to get a "live" feed on the UI).</summary>
public interface IIpCamera
{
    Task<Mat?> GetSnapshotAsync(CancellationToken ct = default);
}
