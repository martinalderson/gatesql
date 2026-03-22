using Microsoft.EntityFrameworkCore;

namespace DbProxy.Data;

public class GateSqlDbContext : DbContext
{
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<QueryLogEntity> QueryLogs => Set<QueryLogEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();

    public GateSqlDbContext(DbContextOptions<GateSqlDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueryLogEntity>(e =>
        {
            e.HasIndex(q => q.Timestamp).IsDescending();
            e.HasIndex(q => q.SessionId);
        });
    }
}
