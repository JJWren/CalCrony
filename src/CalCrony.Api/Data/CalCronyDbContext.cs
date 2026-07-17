using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Data;

public class CalCronyDbContext(DbContextOptions<CalCronyDbContext> options) : DbContext(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(e =>
        {
            e.Property(k => k.Label).HasMaxLength(64);
            e.Property(k => k.KeyHash).HasMaxLength(64);
            e.HasIndex(k => k.KeyHash).IsUnique();
        });
    }
}
