namespace UvssService.Config;

/// <summary>Holds every lane's runtime config (identity AND tuning/
/// simulation settings) loaded ONCE at startup from the Lane Setup MySQL
/// tables (see LaneRuntimeConfigLoader) -- there is deliberately no
/// appsettings.Lanes.json any more; the database is the only source of
/// truth. A plain synchronous singleton rather than an async DB call on
/// every read, since LaneWorkerHostedService/CameraStreamingHostedService
/// need this list synchronously at construction time.</summary>
public class LaneConfigStore
{
    public List<LaneOptions> Lanes { get; set; } = new();
}
