using UvssService.Config;

namespace UvssService.Sensors;

public static class LaneInterlockFactory
{
    public static ILaneInterlock Create(string laneName, InterlockOptions options)
    {
        // Speed is measured from the real loop spacing -- no made-up default
        // distance if the configured positions are missing.
        if (options.LoopPositionsMetres.Count < 2)
        {
            throw new InvalidOperationException(
                $"[{laneName}] LoopPositionsMetres needs at least 2 loop positions (entry and exit), got {options.LoopPositionsMetres.Count}.");
        }
        var positions = options.LoopPositionsMetres;

        // Real hardware only -- a mistyped Source fails loudly at startup.
        return options.Source.Trim().ToLowerInvariant() switch
        {
            "tcp" => new TcpLaneInterlock(laneName, positions, options.ControllerIp, options.TcpPort, options.DebounceMs),
            _ => throw new InvalidOperationException(
                $"[{laneName}] Unsupported InterlockSource '{options.Source}' -- only \"tcp\" (real loop controller) is allowed."),
        };
    }

    public static double EstimateSpeedMetresPerSecond(double distanceMetres, DateTime startTime, DateTime endTime)
    {
        var dt = (endTime - startTime).TotalSeconds;
        return dt <= 0 ? 0.0 : distanceMetres / dt;
    }
}
