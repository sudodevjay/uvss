using Microsoft.EntityFrameworkCore;
using UvssService.Config;

namespace UvssService.Data;

/// <summary>Builds LaneConfigStore's List&lt;LaneOptions&gt; from the Lane
/// Setup MySQL tables (Controllers + DriverCameras + AnprCameras +
/// UvssCameras) instead of appsettings.Lanes.json -- one Controller row IS
/// one lane (LaneId is the join key across all 4 tables), so a lane with no
/// Controller row simply doesn't exist yet. This site only has 3 cameras
/// per lane (driver, ANPR, UVSS/Basler) -- there is no overview camera, so
/// LaneOptions.OverviewCamera is deliberately left at its disabled default
/// here (see IpCameraOptions.Enabled). Reuses the
/// existing LaneOptions/IpCameraOptions/BaslerCameraOptions/InterlockOptions
/// shapes unchanged so LaneWorkerHostedService/CameraStreamingHostedService/
/// LaneInterlockFactory/AreaScanCameraFactory/IpCameraFactory don't need to
/// know or care that their config now comes from a database row instead of
/// a JSON file.</summary>
public static class LaneRuntimeConfigLoader
{
    public static async Task<List<LaneOptions>> LoadAsync(IDbContextFactory<UvssDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var controllers = await db.Controllers.ToListAsync();
        var driverCameras = await db.DriverCameras.ToDictionaryAsync(c => c.LaneId);
        var anprCameras = await db.AnprCameras.ToDictionaryAsync(c => c.LaneId);
        var uvssCameras = await db.UvssCameras.ToDictionaryAsync(c => c.LaneId);

        var lanes = new List<LaneOptions>();
        foreach (var c in controllers)
        {
            var lane = new LaneOptions
            {
                Name = c.LaneId,
                Enabled = c.Enabled,
                Interlock = new InterlockOptions
                {
                    Source = c.InterlockSource,
                    LoopPositionsMetres = ParseDoubles(c.LoopPositionsMetres, new List<double> { 0.0, 3.0 }),
                    ControllerIp = c.Ip,
                    TcpPort = c.TcpPort,
                    DebounceMs = c.DebounceMs,
                    SimulatedIdleSeconds = c.SimulatedIdleSeconds,
                    SimulatedSegmentSeconds = ParseDoubles(c.SimulatedSegmentSeconds, new List<double> { 6.0 }),
                },
            };

            if (driverCameras.TryGetValue(c.LaneId, out var driverCam))
            {
                lane.DriverCamera = ToIpCameraOptions(driverCam.CameraName, driverCam.Ip, driverCam.Username, driverCam.Password, driverCam.Enabled, driverCam.Source, driverCam.TestImageDir);
            }
            if (anprCameras.TryGetValue(c.LaneId, out var anprCam))
            {
                lane.AnprCamera = ToIpCameraOptions(anprCam.CameraName, anprCam.Ip, anprCam.Username, anprCam.Password, anprCam.Enabled, anprCam.Source, anprCam.TestImageDir);
            }
            if (uvssCameras.TryGetValue(c.LaneId, out var uvssCam))
            {
                lane.BaslerCamera = new BaslerCameraOptions
                {
                    Source = uvssCam.Source,
                    DeviceSerial = uvssCam.DeviceSerial,
                    TestImagePath = uvssCam.TestImagePath,
                    SimulatedFramesPerSecond = uvssCam.SimulatedFramesPerSecond,
                    SimulatedFrameHeightPx = uvssCam.SimulatedFrameHeightPx,
                };
            }

            lanes.Add(lane);
        }

        return lanes;
    }

    private static IpCameraOptions ToIpCameraOptions(string name, string ip, string username, string password, bool enabled, string source, string testImageDir) => new()
    {
        Name = name,
        Ip = ip,
        Username = username,
        Password = password,
        Enabled = enabled,
        Source = source,
        TestImageDir = testImageDir,
    };

    /// <summary>Parses a comma-separated list of doubles (e.g. "0,3"), the
    /// format Controller.LoopPositionsMetres/SimulatedSegmentSeconds are
    /// stored in -- falls back to `fallback` if the column is empty so a
    /// blank/unmigrated row still gets a working default.</summary>
    private static List<double> ParseDoubles(string csv, List<double> fallback)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return fallback;
        }
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }
}
