using OpenCvSharp;
using UvssService.Config;
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
    /// <summary>How often the watchdog checks the camera is still there, and
    /// retries opening it when it isn't.</summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    private readonly string _deviceSerial;
    private readonly BaslerCameraOptions _roi; // Roi* sizes of 0 = full sensor
    // Every touch of _camera goes through this lock -- the watchdog timer
    // may dispose/replace it while the lane worker is mid-GrabFrame.
    private readonly object _sync = new();
    private readonly Timer _watchdog;
    private Camera? _camera;
    private bool _grabbing;
    private bool _reportedDisconnect;

    /// <summary>A GigE camera can disappear at any time (cable pulled, PoE
    /// switch rebooted, power cut) -- once pylon reports it removed, that
    /// Camera object is dead for good. Opening only once in the constructor
    /// left the lane stuck on "physically removed" until the whole service
    /// was restarted, so a watchdog re-opens it by serial as soon as it's
    /// reachable again (also covering a camera that wasn't connected yet at
    /// startup).</summary>
    public BaslerAreaScanCamera(BaslerCameraOptions options)
    {
        _deviceSerial = options.DeviceSerial;
        _roi = options;
        lock (_sync)
        {
            TryOpen();
        }
        _watchdog = new Timer(_ => Watchdog(), null, ReconnectInterval, ReconnectInterval);
    }

    public bool Available
    {
        get
        {
            lock (_sync)
            {
                return IsUsable;
            }
        }
    }

    private bool IsUsable => _camera != null && _camera.IsOpen && _camera.IsConnected;

    private void Watchdog()
    {
        lock (_sync)
        {
            if (IsUsable)
            {
                return;
            }
            if (_camera != null && !_reportedDisconnect)
            {
                Console.WriteLine($"BaslerAreaScanCamera: camera (serial='{_deviceSerial}') lost -- will keep retrying every {ReconnectInterval.TotalSeconds:F0}s.");
                _reportedDisconnect = true;
            }
            TryOpen();
        }
    }

    /// <summary>Caller must hold _sync.</summary>
    private void TryOpen()
    {
        CloseCamera();
        Camera? camera = null;
        try
        {
            // No "first camera found" fallback -- with several Basler cameras
            // on one switch that could silently grab the wrong lane's camera.
            if (string.IsNullOrWhiteSpace(_deviceSerial))
            {
                throw new InvalidOperationException("DeviceSerial is not set in Lane Setup.");
            }
            camera = new Camera(FindBySerial(_deviceSerial));
            camera.CameraOpened += Configure;
            camera.Open();
            _camera = camera;
            if (_reportedDisconnect)
            {
                Console.WriteLine($"BaslerAreaScanCamera: camera (serial='{_deviceSerial}') reconnected.");
            }
            _reportedDisconnect = false;
            // Reconnected mid-pass -- resume grabbing where the lane worker
            // left off instead of waiting for the next pass's Start().
            if (_grabbing)
            {
                _camera.StreamGrabber.Start();
            }
        }
        catch (Exception ex)
        {
            if (!_reportedDisconnect)
            {
                Console.WriteLine($"BaslerAreaScanCamera: failed to open camera (serial='{_deviceSerial}'): {ex.Message}");
                _reportedDisconnect = true;
            }
            camera?.Dispose();
            _camera = null;
        }
    }

    /// <summary>Caller must hold _sync.</summary>
    private void CloseCamera()
    {
        if (_camera == null)
        {
            return;
        }
        try
        {
            _camera.Close();
        }
        catch
        {
            // Already gone (device removed) -- nothing left to close.
        }
        _camera.Dispose();
        _camera = null;
    }

    private static ICameraInfo FindBySerial(string serial)
    {
        var match = CameraFinder.Enumerate().FirstOrDefault(info => info[CameraInfoKey.SerialNumber] == serial);
        if (match == null)
        {
            throw new InvalidOperationException($"No Basler camera found with serial '{serial}'.");
        }
        return match;
    }

    /// <summary>Sets one ROI axis: offset 0 first so any size fits, then the
    /// size (0 = sensor maximum), then the offset clamped so offset + size
    /// stays on the sensor. Both snap to the camera's increments.</summary>
    private static void SetRoiAxis(Camera camera, IntegerName offsetName, IntegerName sizeName, int offset, int size)
    {
        camera.Parameters[offsetName].SetValue(0);
        var max = camera.Parameters[sizeName].GetMaximum();
        camera.Parameters[sizeName].SetValue(size > 0 ? Math.Min(size, max) : max, IntegerValueCorrection.Nearest);
        var actual = camera.Parameters[sizeName].GetValue();
        camera.Parameters[offsetName].SetValue(Math.Clamp(offset, 0, max - actual), IntegerValueCorrection.Nearest);
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

        // Start from the factory defaults every time, so whatever was
        // changed in pylon Viewer (ROI, trigger mode, mirroring, gamma...)
        // can never leave the camera in a state this pipeline doesn't expect.
        try
        {
            camera.Parameters[PLCamera.UserSetSelector].SetValue(PLCamera.UserSetSelector.Default);
            camera.Parameters[PLCamera.UserSetLoad].Execute();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: loading factory defaults failed (continuing with current camera settings): {ex.Message}");
        }

        // Only the configured ROI (the area the vehicle passes over),
        // free-running (frames come continuously; the loop interlock decides
        // which ones belong to a pass), no mirroring.
        try
        {
            SetRoiAxis(camera, PLCamera.OffsetX, PLCamera.Width, _roi.RoiOffsetX, _roi.RoiWidth);
            SetRoiAxis(camera, PLCamera.OffsetY, PLCamera.Height, _roi.RoiOffsetY, _roi.RoiHeight);
            Console.WriteLine(
                $"BaslerAreaScanCamera: ROI offsetX={camera.Parameters[PLCamera.OffsetX].GetValue()} width={camera.Parameters[PLCamera.Width].GetValue()} " +
                $"offsetY={camera.Parameters[PLCamera.OffsetY].GetValue()} height={camera.Parameters[PLCamera.Height].GetValue()}.");
            camera.Parameters[PLCamera.TriggerSelector].SetValue(PLCamera.TriggerSelector.FrameStart);
            camera.Parameters[PLCamera.TriggerMode].SetValue(PLCamera.TriggerMode.Off);
            camera.Parameters[PLCamera.ReverseX].SetValue(false);
            camera.Parameters[PLCamera.ReverseY].SetValue(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: ROI/trigger configuration failed (continuing with camera defaults): {ex.Message}");
        }

        // Auto brightness, but with exposure capped so a moving chassis
        // doesn't smear: the camera lengthens exposure up to the cap first,
        // then adds gain. 2 ms is ~17 mm of motion at 30 km/h; lower the cap
        // once the under-vehicle illuminator is installed.
        try
        {
            camera.Parameters[PLCamera.AutoFunctionProfile].SetValue(PLCamera.AutoFunctionProfile.MinimizeGain);
            camera.Parameters[PLCamera.AutoTargetBrightness].SetValue(0.5);
            camera.Parameters[PLCamera.AutoExposureTimeLowerLimit].SetValue(100.0);
            camera.Parameters[PLCamera.AutoExposureTimeUpperLimit].SetValue(2000.0);
            camera.Parameters[PLCamera.AutoGainLowerLimit].SetValue(0.0);
            camera.Parameters[PLCamera.AutoGainUpperLimit].SetValue(24.0);
            camera.Parameters[PLCamera.ExposureAuto].SetValue(PLCamera.ExposureAuto.Continuous);
            camera.Parameters[PLCamera.GainAuto].SetValue(PLCamera.GainAuto.Continuous);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: auto exposure/gain configuration failed (continuing with camera defaults): {ex.Message}");
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

        // Jumbo packets: with the default 1500-byte packets the onboard
        // I219-LM drops packets at full resolution. Needs the PC adapter's
        // Jumbo Packet set to 9014 (pylon GigE Configurator auto-opt). The
        // camera forgets this on power cycle, so set it on every open.
        try
        {
            camera.Parameters[PLCamera.GevSCPSPacketSize].SetValue(8192);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: PacketSize configuration failed (continuing with camera defaults): {ex.Message}");
        }

        // Fixed 30 fps (comfortably inside GigE bandwidth at full
        // resolution) and a deep buffer pool, so a momentarily busy grab
        // loop doesn't drop frames as "incompletely grabbed".
        try
        {
            camera.Parameters[PLCamera.AcquisitionFrameRateEnable].SetValue(true);
            camera.Parameters[PLCamera.AcquisitionFrameRate].SetValue(30.0);
            camera.Parameters[PLCameraInstance.MaxNumBuffer].SetValue(64);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BaslerAreaScanCamera: frame rate/buffer configuration failed (continuing with camera defaults): {ex.Message}");
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            _grabbing = true;
            if (!IsUsable)
            {
                return;
            }
            try
            {
                _camera!.StreamGrabber.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BaslerAreaScanCamera: Start failed: {ex.Message}");
            }
        }
    }

    public Mat? GrabFrame(int timeoutMs = 200)
    {
        lock (_sync)
        {
            return GrabFrameLocked(timeoutMs);
        }
    }

    private Mat? GrabFrameLocked(int timeoutMs)
    {
        if (_camera == null || !_camera.StreamGrabber.IsGrabbing)
        {
            return null;
        }
        try
        {
            // TimeoutHandling.Return hands back null (not a failed result)
            // when no frame arrived within timeoutMs -- a normal outcome
            // here, not an error.
            using var result = _camera.StreamGrabber.RetrieveResult(timeoutMs, TimeoutHandling.Return);
            if (result == null)
            {
                return null;
            }
            if (!result.GrabSucceeded)
            {
                Console.WriteLine($"BaslerAreaScanCamera: grab failed: {result.ErrorDescription}");
                return null;
            }
            return GrabResultToMat(result);
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
        // Returned as single-channel Mono8 -- this runs once per frame at the
        // camera's full rate, and expanding every frame to 3-channel BGR here
        // was enough extra work to fall behind the camera (buffers overflow,
        // frames arrive "incompletely grabbed"). Consumers that need BGR
        // convert the one image they actually use.
        var gray = new Mat(height, width, MatType.CV_8UC1);
        if (buffer.Length > 0)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, gray.Data, Math.Min(buffer.Length, width * height));
        }
        return gray;
    }

    public void Stop()
    {
        lock (_sync)
        {
            _grabbing = false;
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
    public BaslerAreaScanCamera(BaslerCameraOptions options)
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
