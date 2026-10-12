using UvssService.Config;

namespace UvssService.Cameras;

public static class AreaScanCameraFactory
{
    public static IAreaScanCamera Create(BaslerCameraOptions options)
    {
        // Real hardware only -- a mistyped Source fails loudly at startup.
        return options.Source.Trim().ToLowerInvariant() switch
        {
            "pylon" => new BaslerAreaScanCamera(options.DeviceSerial),
            _ => throw new InvalidOperationException(
                $"Unsupported UVSS camera Source '{options.Source}' -- only \"pylon\" (real Basler camera) is allowed."),
        };
    }
}
