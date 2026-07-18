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
    public DbSet<CalendarConnection> CalendarConnections => Set<CalendarConnection>();
    public DbSet<CalendarLinkToken> CalendarLinkTokens => Set<CalendarLinkToken>();
    public DbSet<WebLoginState> WebLoginStates => Set<WebLoginState>();
    public DbSet<WebRefreshToken> WebRefreshTokens => Set<WebRefreshToken>();
    public DbSet<UserGuildMembership> UserGuildMemberships => Set<UserGuildMembership>();

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
            e.Property(u => u.Username).HasMaxLength(64);
            e.Property(u => u.AvatarHash).HasMaxLength(64);
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

        modelBuilder.Entity<CalendarConnection>(e =>
        {
            e.Property(c => c.EncryptedAccessToken).HasMaxLength(2000);
            e.Property(c => c.EncryptedRefreshToken).HasMaxLength(2000);
            e.HasIndex(c => new { c.UserId, c.Provider }).IsUnique();
        });

        modelBuilder.Entity<CalendarLinkToken>(e =>
        {
            e.Property(t => t.Token).HasMaxLength(64);
            e.HasIndex(t => t.Token).IsUnique();
        });

        modelBuilder.Entity<WebLoginState>(e =>
        {
            e.Property(t => t.Token).HasMaxLength(64);
            e.Property(t => t.ReturnUrl).HasMaxLength(256);
            e.HasIndex(t => t.Token).IsUnique();
        });

        modelBuilder.Entity<WebRefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
        });

        modelBuilder.Entity<UserGuildMembership>(e =>
        {
            e.HasKey(m => new { m.UserId, m.GuildId });
            e.Property(m => m.GuildName).HasMaxLength(128);
            e.Property(m => m.IconHash).HasMaxLength(64);
        });
    }
}
