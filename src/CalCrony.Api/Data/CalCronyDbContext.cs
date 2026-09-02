using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Data;

/// <summary>EF Core context for all CalCrony state; model configuration (lengths, indexes, cascades) lives in OnModelCreating.</summary>
/// <param name="options">The context options.</param>
public class CalCronyDbContext(DbContextOptions<CalCronyDbContext> options) : DbContext(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<GuildRole> GuildRoles => Set<GuildRole>();
    public DbSet<GuildMemberRole> GuildMemberRoles => Set<GuildMemberRole>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<RsvpOption> RsvpOptions => Set<RsvpOption>();
    public DbSet<Rsvp> Rsvps => Set<Rsvp>();
    public DbSet<Poll> Polls => Set<Poll>();
    public DbSet<PollOption> PollOptions => Set<PollOption>();
    public DbSet<PollVote> PollVotes => Set<PollVote>();
    public DbSet<EventNotification> EventNotifications => Set<EventNotification>();
    public DbSet<EventSeries> EventSeries => Set<EventSeries>();
    public DbSet<SeriesNotification> SeriesNotifications => Set<SeriesNotification>();
    public DbSet<EventTemplate> EventTemplates => Set<EventTemplate>();
    public DbSet<EventTemplateNotification> EventTemplateNotifications => Set<EventTemplateNotification>();
    public DbSet<LiveList> LiveLists => Set<LiveList>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<IcsFeedToken> IcsFeedTokens => Set<IcsFeedToken>();
    public DbSet<CalendarConnection> CalendarConnections => Set<CalendarConnection>();
    public DbSet<CalendarLinkToken> CalendarLinkTokens => Set<CalendarLinkToken>();
    public DbSet<WebLoginState> WebLoginStates => Set<WebLoginState>();
    public DbSet<WebRefreshToken> WebRefreshTokens => Set<WebRefreshToken>();
    public DbSet<UserGuildMembership> UserGuildMemberships => Set<UserGuildMembership>();
    public DbSet<ActionLogEntry> ActionLogEntries => Set<ActionLogEntry>();

    /// <summary>Max lengths, indexes (including the partial unique live-occurrence index), and cascade rules.</summary>
    /// <param name="modelBuilder">The EF model builder.</param>
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
            e.Property(g => g.Name).HasMaxLength(FieldLimits.GuildName);
            // The anonymous calendar route resolves guilds by slug; unique so a slug can never
            // name two servers (Postgres treats NULLs as distinct, so "off" rows don't collide).
            e.Property(g => g.PublicCalendarSlug).HasMaxLength(64);
            e.HasIndex(g => g.PublicCalendarSlug).IsUnique();
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.Name).HasMaxLength(FieldLimits.ChannelName);
            e.HasIndex(c => c.GuildId);
        });

        modelBuilder.Entity<GuildRole>(e =>
        {
            e.HasKey(r => new { r.GuildId, r.RoleId });
            e.Property(r => r.Name).HasMaxLength(FieldLimits.RoleName);
        });

        modelBuilder.Entity<GuildMemberRole>(e =>
        {
            // One row per member per guild; the role set is a Postgres bigint[] (Npgsql maps
            // long[] natively), so "which watched roles" is one column, not a join table.
            e.HasKey(m => new { m.GuildId, m.UserId });
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
            // Caps come from FieldLimits so endpoint validation and the schema can't drift.
            e.Property(ev => ev.Title).HasMaxLength(FieldLimits.EventTitle);
            e.Property(ev => ev.Description).HasMaxLength(FieldLimits.EventDescription);
            e.Property(ev => ev.TimeZone).HasMaxLength(64);
            e.Property(ev => ev.Location).HasMaxLength(FieldLimits.EventLocation);
            e.Property(ev => ev.ImageUrl).HasMaxLength(FieldLimits.EventImageUrl);
            e.HasIndex(ev => new { ev.GuildId, ev.StartsAt });
            // The CSV export walks a guild's events by id; without this the keyset scans the
            // global id index and discards other guilds' rows (every tenant's history).
            e.HasIndex(ev => new { ev.GuildId, ev.Id }, "IX_Events_GuildId_Id");
            e.HasMany(ev => ev.Options).WithOne().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Rsvps).WithOne().HasForeignKey(r => r.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(ev => ev.Notifications).WithOne().HasForeignKey(n => n.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ev => ev.Series).WithMany().HasForeignKey(ev => ev.SeriesId).OnDelete(DeleteBehavior.SetNull);
            // Named separately or the second HasIndex on the same column replaces the first.
            e.HasIndex(ev => ev.SeriesId, "IX_Events_SeriesId");
            // The rolling-occurrence invariant and the concurrent-spawn guard: at most one live
            // (Scheduled=0 / Started=1) occurrence per series. NULL SeriesId rows are exempt.
            e.HasIndex(ev => ev.SeriesId, "IX_Events_SeriesId_Live")
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1)");
        });

        modelBuilder.Entity<EventSeries>(e =>
        {
            e.Property(s => s.Title).HasMaxLength(FieldLimits.EventTitle);
            e.Property(s => s.Description).HasMaxLength(FieldLimits.EventDescription);
            e.Property(s => s.TimeZone).HasMaxLength(64);
            e.Property(s => s.Location).HasMaxLength(FieldLimits.EventLocation);
            e.Property(s => s.ImageUrl).HasMaxLength(FieldLimits.EventImageUrl);
            // Sized for the serialization worst case, not the typical one: System.Text.Json
            // escapes astral-plane chars (emoji) six-to-one per UTF-16 unit even with the relaxed
            // encoder RsvpPolicy.SerializeSpecs uses, so 10 options × two all-emoji 64-unit
            // fields ≈ 8.4k chars, plus a restriction of at most RsvpPolicy.MaxAllowedRoles
            // snowflakes per option (≈1.2k more) — that cap exists to keep this bound. Control
            // characters are banned at validation.
            e.Property(s => s.RsvpOptionsJson).HasMaxLength(10240);
            e.HasIndex(s => s.GuildId);
            e.HasMany(s => s.NotificationSpecs).WithOne().HasForeignKey(n => n.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SeriesNotification>(e =>
        {
            e.Property(n => n.Message).HasMaxLength(FieldLimits.NotificationMessage);
            e.Property(n => n.Mentions).HasMaxLength(FieldLimits.NotificationMentions);
        });

        modelBuilder.Entity<EventTemplate>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(64);
            e.Property(t => t.Title).HasMaxLength(FieldLimits.EventTitle);
            e.Property(t => t.Description).HasMaxLength(FieldLimits.EventDescription);
            e.Property(t => t.Location).HasMaxLength(FieldLimits.EventLocation);
            e.Property(t => t.ImageUrl).HasMaxLength(FieldLimits.EventImageUrl);
            // Uniqueness is enforced case-insensitively by a functional unique index on
            // (GuildId, lower(Name)), created via raw SQL in the AddEventTemplates migration —
            // EF's fluent API can't express expression indexes.
            e.HasMany(t => t.Notifications).WithOne().HasForeignKey(n => n.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EventTemplateNotification>(e =>
        {
            e.Property(n => n.Message).HasMaxLength(FieldLimits.NotificationMessage);
            e.Property(n => n.Mentions).HasMaxLength(FieldLimits.NotificationMentions);
        });

        modelBuilder.Entity<RsvpOption>(e =>
        {
            e.Property(o => o.Emote).HasMaxLength(64);
            e.Property(o => o.Label).HasMaxLength(64);
        });

        modelBuilder.Entity<Rsvp>(e =>
        {
            // One RSVP per user per OPTION: a member may hold several options on an event that
            // allows multiple RSVPs. Single-choice mode (the default) is enforced by PutRsvp
            // under the event's row lock, not by the index — the way capacity is.
            e.HasIndex(r => new { r.EventId, r.UserId, r.OptionId }).IsUnique();
            // The CSV export pages RSVPs by (EventId, Id); the unique index above can't serve
            // that walk, so a crowded event would re-sort its whole RSVP set per page.
            e.HasIndex(r => new { r.EventId, r.Id }, "IX_Rsvps_EventId_Id");
        });

        modelBuilder.Entity<Poll>(e =>
        {
            e.Property(p => p.Question).HasMaxLength(252);
            e.Property(p => p.TimeZone).HasMaxLength(64);
            e.HasIndex(p => new { p.GuildId, p.CreatedAt });
            e.HasIndex(p => new { p.Status, p.ClosesAt });
            e.HasMany(p => p.Options).WithOne().HasForeignKey(o => o.PollId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Votes).WithOne().HasForeignKey(v => v.PollId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PollOption>(e =>
        {
            e.Property(o => o.Text).HasMaxLength(100);
        });

        modelBuilder.Entity<PollVote>(e =>
        {
            e.HasIndex(v => new { v.PollId, v.UserId, v.OptionId }).IsUnique();
        });

        modelBuilder.Entity<EventNotification>(e =>
        {
            e.Property(n => n.Message).HasMaxLength(FieldLimits.NotificationMessage);
            e.Property(n => n.Mentions).HasMaxLength(FieldLimits.NotificationMentions);
        });

        modelBuilder.Entity<LiveList>(e =>
        {
            // One live list per channel — the unique index backstops the endpoint's pre-check
            // against concurrent creates.
            e.HasIndex(l => l.ChannelId).IsUnique();
            e.HasIndex(l => l.GuildId);
        });

        modelBuilder.Entity<Delivery>(e =>
        {
            e.Property(d => d.PayloadJson).HasMaxLength(8192);
            e.HasIndex(d => d.RecipientUserId);
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

        modelBuilder.Entity<ActionLogEntry>(e =>
        {
            e.Property(a => a.Summary).HasMaxLength(FieldLimits.ActionSummary);
            e.Property(a => a.DetailsJson).HasMaxLength(FieldLimits.ActionDetails);
            // The activity page reads one guild newest-first with a keyset cursor; the retention
            // purge filters on CreatedAt alone, which the composite's second column can't serve
            // (Postgres can't skip-scan the leading GuildId), so it gets its own index.
            e.HasIndex(a => new { a.GuildId, a.CreatedAt }).IsDescending(false, true);
            e.HasIndex(a => a.CreatedAt);
        });
    }
}
