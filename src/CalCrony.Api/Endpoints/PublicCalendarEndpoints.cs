using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>The opt-in public web calendar: per-guild on/off with an unguessable, regenerable slug
/// (the slug is the credential — the calendar route itself is anonymous, like the ICS feed), and
/// the login-free month view it serves. Default off: the privacy stance is the product.</summary>
public static partial class PublicCalendarEndpoints
{
    /// <summary>Web-app path prefix the slug hangs off.</summary>
    public const string PathPrefix = "/c/";

    /// <summary>Slugs are 128 random bits as lowercase hex; anything else 404s without a lookup.</summary>
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex SlugShape();

    /// <summary>Maps the settings and calendar routes.</summary>
    /// <param name="app">The route builder to map onto.</param>
    public static void MapPublicCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guilds/{guildId:long}/public-calendar", GetSettings);
        app.MapPut("/guilds/{guildId:long}/public-calendar", PutSettings);

        // Anonymous by design — the unguessable slug IS the credential.
        app.MapGet("/public/calendars/{slug}", GetCalendar).AllowAnonymous();
    }

    /// <summary>Returns the guild's public-calendar state (any member may read it — the link is
    /// meant to be shared).</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="db">The database context.</param>
    /// <param name="configuration">The application configuration (web origin for absolute links).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetSettings(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        CalCronyDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await db.Guilds.FindAsync([guildId], cancellationToken);
        return Results.Ok(ToSettingsDto(guild?.PublicCalendarSlug, configuration));
    }

    /// <summary>Turns the public calendar on or off (managers only). Enabling mints a slug on first
    /// use and keeps it afterwards; <c>Regenerate</c> mints a fresh one, revoking every old link.</summary>
    /// <param name="context">The current HTTP request context (carries the caller identity).</param>
    /// <param name="access">The guild-membership guard service.</param>
    /// <param name="guildId">The Discord guild (server) id.</param>
    /// <param name="request">The request body.</param>
    /// <param name="db">The database context.</param>
    /// <param name="configuration">The application configuration (web origin for absolute links).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> PutSettings(
        HttpContext context,
        GuildAccessService access,
        long guildId,
        PublicCalendarRequest request,
        CalCronyDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildManageAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var guild = await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);
        if (!request.Enabled)
        {
            guild.PublicCalendarSlug = null;
        }
        else if (guild.PublicCalendarSlug is null || request.Regenerate)
        {
            guild.PublicCalendarSlug = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSettingsDto(guild.PublicCalendarSlug, configuration));
    }

    /// <summary>Serves one month of the public calendar: the guild's non-cancelled events plus
    /// projected future occurrences of its running series, in the guild's zone. Unknown, malformed,
    /// and switched-off slugs all 404 identically; names come from bot snapshots only (ADR 0001).</summary>
    /// <param name="context">The current HTTP request context.</param>
    /// <param name="slug">The calendar slug from the URL.</param>
    /// <param name="year">The month's year (defaults to the current month in the guild's zone).</param>
    /// <param name="month">The month, 1-12 (defaults with <paramref name="year"/>).</param>
    /// <param name="db">The database context.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route response; failure statuses follow the rules described in the summary.</returns>
    private static async Task<IResult> GetCalendar(
        HttpContext context,
        string slug,
        int? year,
        int? month,
        CalCronyDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!SlugShape().IsMatch(slug))
        {
            return Results.NotFound();
        }

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.PublicCalendarSlug == slug, cancellationToken);
        if (guild is null)
        {
            return Results.NotFound();
        }

        var zone = Mapping.FindZone(guild.TimeZone) ?? DateTimeZone.Utc;
        var now = clock.GetCurrentInstant();
        var today = now.InZone(zone).Date;
        var y = year ?? today.Year;
        var m = month ?? today.Month;
        if (!PublicCalendarBuilder.IsMonthInRange(y, m, today))
        {
            return Results.BadRequest(new ErrorResponse(
                $"Pick a month within {PublicCalendarBuilder.MaxMonthsFromNow / 12} years of today (month 1-12)."));
        }

        var (windowStart, windowEnd) = PublicCalendarBuilder.MonthWindow(zone, y, m);
        var events = await db.Events
            .Where(e => e.GuildId == guild.Id
                        && e.Status != EventStatus.Cancelled
                        && e.StartsAt >= windowStart
                        && e.StartsAt < windowEnd)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(cancellationToken);
        var series = await db.EventSeries
            .Where(s => s.GuildId == guild.Id && !s.Ended)
            .ToListAsync(cancellationToken);
        var channelIds = events.Select(e => e.ChannelId).Concat(series.Select(s => s.ChannelId)).Distinct().ToList();
        var channelNames = await db.Channels
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        // Shareable, not discoverable — the web page carries the same directive for crawlers. And
        // never cached: the slug is a revocable credential, so a disabled or regenerated link must
        // not keep answering from an HTTP cache.
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(PublicCalendarBuilder.Build(guild.Name, zone, y, m, events, series, channelNames, now));
    }

    private static PublicCalendarSettingsDto ToSettingsDto(string? slug, IConfiguration configuration)
    {
        if (slug is null)
        {
            return new PublicCalendarSettingsDto(false, null, null, null);
        }

        var path = $"{PathPrefix}{slug}";
        var origin = WebOrigin.Resolve(configuration);
        return new PublicCalendarSettingsDto(true, slug, path, origin is null ? null : origin + path);
    }
}
