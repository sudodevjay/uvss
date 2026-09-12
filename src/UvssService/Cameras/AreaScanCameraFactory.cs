using UvssService.Config;

namespace UvssService.Cameras;

public static class AreaScanCameraFactory
{
    public static IAreaScanCamera Create(
        BaslerCameraOptions options, int frameWidthPx, int frameHeightPx, SimulatedVehicleSlot vehicleSlot)
    {
        return options.Source.Trim().ToLowerInvariant() switch
        {
            "pylon" => new BaslerAreaScanCamera(options.DeviceSerial),
            _ => new SimulatedAreaScanCamera(
                options.TestImagePath, frameWidthPx, frameHeightPx, vehicleSlot, options.SimulatedFramesPerSecond),
        };
    }
}
