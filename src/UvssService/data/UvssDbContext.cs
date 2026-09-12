using Microsoft.EntityFrameworkCore;
using UvssService.Data.Entities;

namespace UvssService.Data;

/// <summary>Lane Setup's storage (controllers/cameras) plus the permanent
/// scan/event history (ScanEvents) -- all MySQL rows, not appsettings/JSON
/// files or an in-memory cache, so they can be added, edited, deleted, and
/// (for ScanEvents) reported on at any time without a deploy or a running
/// service.</summary>
public class UvssDbContext : DbContext
{
    public UvssDbContext(DbContextOptions<UvssDbContext> options) : base(options) { }

    public DbSet<Controller> Controllers => Set<Controller>();
    public DbSet<DriverCamera> DriverCameras => Set<DriverCamera>();
    public DbSet<AnprCamera> AnprCameras => Set<AnprCamera>();
    public DbSet<UvssCamera> UvssCameras => Set<UvssCamera>();
    public DbSet<ScanEvent> ScanEvents => Set<ScanEvent>();
}
