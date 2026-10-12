using UvssService.Config;

namespace UvssService.Cameras;

public static class IpCameraFactory
{
    public static IIpCamera Create(IpCameraOptions options, CameraDefaultsOptions defaults)
    {
        // Real hardware only -- a mistyped Source fails loudly at startup.
        return options.Source.Trim().ToLowerInvariant() switch
        {
            "axis" => new AxisIpCamera(options, defaults),
            _ => throw new InvalidOperationException(
                $"Unsupported camera Source '{options.Source}' for '{options.Name}' -- only \"axis\" (real IP camera) is allowed."),
        };
    }
}
