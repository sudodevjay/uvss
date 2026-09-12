using OpenCvSharp;
#if PYLON_AVAILABLE
using Basler.Pylon;
#endif

namespace UvssService.Cameras;

#if PYLON_AVAILABLE

/// <summary>Real Basler pylon SDK integration for a physical area-scan (ace
/// series) camera -- compiled in only when UvssService.csproj found a real
/// Basler.Pylon.dll (see its PylonDotNetDir comment) and defined
/// PYLON_AVAILABLE.
///
/// The parameter/enum names used below (PixelFormat, AcquisitionMode) were
/// confirmed by reflecting over the actual installed Basler.Pylon.dll
/// (pylon 26.08 SDK) on the dev machine this was written on -- they are
/// real, existing members of that assembly, not a guess.
///
/// This site has no hardware encoder -- the camera free-runs at a fixed
/// frame rate, and CorrelationResampler's software-side correction
/// (LaneWorkerHostedService) is what corrects for a vehicle that speeds up,
/// slows down, or briefly stops mid-pass.</summary>
public class BaslerAreaScanCamera : IAreaScanCamera
{
    private readonly Camera? _camera;

    public BaslerAreaScanCamera(string deviceSerial)
    {
        try
        {
            _camera = string.IsNullOrWhiteSpace(deviceSerial)
                ? new Camera()
                : new Camera(FindBySerial(deviceSerial));
            _camera.CameraOpened += Configure;
            _camera.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: failed to open camera (serial='{deviceSerial}'): {ex.Message}");
            _camera?.Dispose();
            _camera = null;
        }
    }

    public bool Available => _camera != null;

    private static ICameraInfo FindBySerial(string serial)
    {
        var match = CameraFinder.Enumerate().FirstOrDefault(info => info[CameraInfoKey.SerialNumber] == serial);
        if (match == null)
        {
            throw new InvalidOperationException($"No Basler camera found with serial '{serial}'.");
        }
        return match;
    }

    /// <summary>Runs once, right after Camera.Open() -- the documented
    /// pylon .NET pattern for setting acquisition parameters before
    /// grabbing starts.</summary>
    private void Configure(object? sender, EventArgs e)
    {
        if (sender is not Camera camera)
        {
            return;
        }
        try
        {
            camera.Parameters[PLCamera.PixelFormat].SetValue(PLCamera.PixelFormat.Mono8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: PixelFormat configuration failed (continuing with camera defaults): {ex.Message}");
        }

        try
        {
            camera.Parameters[PLCamera.AcquisitionMode].SetValue(PLCamera.AcquisitionMode.Continuous);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: AcquisitionMode configuration failed (continuing with camera defaults): {ex.Message}");
        }
    }

    public void Start()
    {
        if (_camera == null)
        {
            return;
        }
        try
        {
            _camera.StreamGrabber.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: Start failed: {ex.Message}");
        }
    }

    public Mat? GrabFrame(int timeoutMs = 200)
    {
        if (_camera == null || !_camera.StreamGrabber.IsGrabbing)
        {
            return null;
        }
        try
        {
            using var result = _camera.StreamGrabber.RetrieveResult(timeoutMs, TimeoutHandling.Return);
            return result.GrabSucceeded ? GrabResultToMat(result) : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: GrabFrame failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>pylon hands back pixel data as a raw buffer keyed by
    /// PixelTypeValue -- Mono8 (set in Configure above) is handled here as
    /// single-channel grayscale, then converted to BGR since the rest of
    /// this pipeline (FrameStitcher, EncodeUnderVehicleJpeg) expects
    /// 3-channel Mats throughout, same as every other camera source. Width
    /// and Height both come from the actual grabbed frame, so this handles
    /// whatever ROI the camera is configured for.</summary>
    private static Mat GrabResultToMat(IGrabResult result)
    {
        var width = (int)result.Width;
        var height = Math.Max((int)result.Height, 1);
        var buffer = result.PixelData as byte[] ?? Array.Empty<byte>();
        using var gray = new Mat(height, width, MatType.CV_8UC1);
        if (buffer.Length > 0)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, gray.Data, buffer.Length);
        }
        var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }

    public void Stop()
    {
        if (_camera == null)
        {
            return;
        }
        try
        {
            _camera.StreamGrabber.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: Stop failed: {ex.Message}");
        }
    }
}

#else

/// <summary>Placeholder used whenever the real pylon SDK isn't available at
/// build time (see UvssService.csproj's PylonDotNetDir/PYLON_AVAILABLE
/// comment) -- e.g. this dev machine, which has neither the SDK installed
/// nor the camera attached. On the machine with the camera physically
/// connected: install Basler's pylon SDK, point PylonDotNetDir at its
/// Basler.Pylon.dll, and rebuild -- that switches this file over to the
/// real implementation above automatically, no code changes needed here.</summary>
public class BaslerAreaScanCamera : IAreaScanCamera
{
    public BaslerAreaScanCamera(string deviceSerial)
    {
        Console.WriteLine(
            "BaslerAreaScanCamera: built without the pylon SDK (PYLON_AVAILABLE not defined) -- "
            + "see UvssService.csproj's PylonDotNetDir comment. Falling back to unavailable."
        );
    }

    public bool Available => false;

    public void Start() { }

    public Mat? GrabFrame(int timeoutMs = 200) => null;

    public void Stop() { }
}

#endif
