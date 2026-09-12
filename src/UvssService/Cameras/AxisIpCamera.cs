using System.Net;
using OpenCvSharp;
using UvssService.Config;

namespace UvssService.Cameras;

/// <summary>Fetches one JPEG snapshot from an Axis-style VAPIX endpoint over
/// HTTP with Digest auth -- the .NET analogue of anpr-ai-service's
/// get_snapshot()/camera_url()/digest_auth() pattern.
///
/// NOTE: relies on .NET's built-in Digest auth support in
/// HttpClientHandler.Credentials (SocketsHttpHandler on modern .NET
/// negotiates Basic/Digest from the server's WWW-Authenticate challenge).
/// This hasn't been verified against a real Axis camera yet -- if it turns
/// out not to negotiate correctly, swap in a manual digest-handshake
/// implementation here without changing the IIpCamera contract.</summary>
public class AxisIpCamera : IIpCamera, IDisposable
{
    private readonly IpCameraOptions _camera;
    private readonly CameraDefaultsOptions _defaults;
    private readonly HttpClient _http;

    public AxisIpCamera(IpCameraOptions camera, CameraDefaultsOptions defaults)
    {
        _camera = camera;
        _defaults = defaults;

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(camera.Username) || !string.IsNullOrEmpty(camera.Password))
        {
            handler.Credentials = new NetworkCredential(camera.Username, camera.Password);
        }
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(defaults.SnapshotTimeoutSeconds),
        };
    }

    public async Task<Mat?> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!_camera.Enabled)
        {
            return null;
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(BuildUrl(), ct);
            return Cv2.ImDecode(bytes, ImreadModes.Color);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CameraUnavailableException(ex.Message, ex);
        }
    }

    private string BuildUrl()
    {
        var baseUrl = _camera.Ip.StartsWith("http://") || _camera.Ip.StartsWith("https://")
            ? _camera.Ip.TrimEnd('/')
            : $"http://{_camera.Ip}";
        var path = _defaults.SnapshotPath.TrimStart('/');
        var query = $"resolution={Uri.EscapeDataString(_defaults.SnapshotResolution)}&compression={_defaults.SnapshotCompression}";
        return $"{baseUrl}/{path}?{query}";
    }

    public void Dispose() => _http.Dispose();
}
