using UvssService.Config;

namespace UvssService.Sensors;

public static class LaneInterlockFactory
{
    public static ILaneInterlock Create(string laneName, InterlockOptions options)
    {
        var positions = options.LoopPositionsMetres.Count >= 2
            ? options.LoopPositionsMetres
            : new List<double> { 0.0, 3.0 };

        var segmentSeconds = options.SimulatedSegmentSeconds.Count == positions.Count - 1
            ? options.SimulatedSegmentSeconds
            : Enumerable.Repeat(1.0, positions.Count - 1).ToList();

        return options.Source.Trim().ToLowerInvariant() switch
        {
            "tcp" => new TcpLaneInterlock(laneName, positions, options.ControllerIp, options.TcpPort, options.DebounceMs),
            _ => new SimulatedLaneInterlock(laneName, positions, options.SimulatedIdleSeconds, segmentSeconds),
        };
    }

    public static double EstimateSpeedMetresPerSecond(double distanceMetres, DateTime startTime, DateTime endTime)
    {
        var dt = (endTime - startTime).TotalSeconds;
        return dt <= 0 ? 0.0 : distanceMetres / dt;
    }
}
