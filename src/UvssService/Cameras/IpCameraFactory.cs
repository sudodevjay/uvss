using UvssService.Config;

namespace UvssService.Cameras;

public static class IpCameraFactory
{
    public static IIpCamera Create(IpCameraOptions options, CameraDefaultsOptions defaults, SimulatedVehicleSlot vehicleSlot)
    {
        return options.Source.Equals("simulated", StringComparison.OrdinalIgnoreCase)
            ? new SimulatedIpCamera(options.TestImageDir, vehicleSlot)
            : new AxisIpCamera(options, defaults);
    }
}
