using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Data;

public class CalCronyDbContext(DbContextOptions<CalCronyDbContext> options) : DbContext(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<RsvpOption> RsvpOptions => Set<RsvpOption>();
    public DbSet<Rsvp> Rsvps => Set<Rsvp>();
    public DbSet<EventNotification> EventNotifications => Set<EventNotification>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<IcsFeedToken> IcsFeedTokens => Set<IcsFeedToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(e =>
        {
            e.Property(k => k.Label).HasMaxLength(64);
            e.Property(k => k.KeyHash).HasMaxLength(64);
            e.HasIndex(k => k.KeyHash).IsUnique();
        });

        modelBuilder.Entity<Guild>(e =>
        {
            e.Property(g => g.Id).ValueGeneratedNever();
            e.Property(g => g.TimeZone).HasMaxLength(64);
        });

        modelBuilder.Entity<UserProfile>(e =>
        {
            e.Property(u => u.Id).ValueGeneratedNever();
            e.Property(u => u.TimeZone).HasMaxLength(64);
        });

        modelBuilder.Entity<Event>(e =>
        {
            e.Property(ev => ev.Title).HasMaxLength(128);
            e.Property(ev => ev.Description).HasMaxLength(4096);
            e.Property(ev => ev.TimeZone).HasMaxLength(64);
            e.Property(ev => ev.Location).HasMaxLength(256);
            e.Property(ev => ev.ImageUrl).HasMaxLength(512);
            e.HasIndex(ev => new { ev.GuildId, ev.StartsAt });
            e.HasMany(ev => ev.Options).WithOne().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Rsvps).WithOne().HasForeignKey(r => r.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Notifications).WithOne().HasForeignKey(n => n.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RsvpOption>(e =>
        {
            e.Property(o => o.Emote).HasMaxLength(64);
            e.Property(o => o.Label).HasMaxLength(64);
        });

        modelBuilder.Entity<Rsvp>(e =>
        {
            // One RSVP per user per event in v1; multi-select is a later premium-parity feature.
            e.HasIndex(r => new { r.EventId, r.UserId }).IsUnique();
        });

        modelBuilder.Entity<EventNotification>(e =>
        {
            e.Property(n => n.Message).HasMaxLength(1024);
            e.Property(n => n.Mentions).HasMaxLength(256);
        });

        modelBuilder.Entity<Delivery>(e =>
        {
            e.Property(d => d.PayloadJson).HasMaxLength(8192);
            e.HasIndex(d => new { d.Status, d.DueAt });
        });

        modelBuilder.Entity<IcsFeedToken>(e =>
        {
            e.Property(t => t.Token).HasMaxLength(64);
            e.HasIndex(t => t.Token).IsUnique();
            e.HasIndex(t => t.GuildId).IsUnique();
        });
    }
}
