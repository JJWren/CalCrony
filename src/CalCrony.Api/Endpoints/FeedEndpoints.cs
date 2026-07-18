using System.Security.Cryptography;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CalCrony.Api.Endpoints;

public static class FeedEndpoints
{
    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        // Authenticated (bot, or a web member of the guild): mints/returns the guild's feed token.
        app.MapPost("/guilds/{guildId:long}/feed-token", GetOrCreateToken);

        // Anonymous by design — the unguessable token IS the credential.
        app.MapGet("/feeds/{token}.ics", GetFeed).AllowAnonymous();
    }

    private static async Task<IResult> GetOrCreateToken(
        HttpContext context, GuildAccessService access, long guildId, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        if (await EventEndpoints.GuardGuildReadAsync(context, access, guildId, cancellationToken) is { } denied)
        {
            return denied;
        }

        await EventEndpoints.GetOrCreateGuildAsync(db, guildId, cancellationToken);

        var existing = await db.IcsFeedTokens.FirstOrDefaultAsync(t => t.GuildId == guildId, cancellationToken);
        if (existing is null)
        {
            existing = new IcsFeedToken
            {
                Id = Guid.NewGuid(),
                GuildId = guildId,
                Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)),
                CreatedAt = clock.GetCurrentInstant(),
            };
            db.IcsFeedTokens.Add(existing);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new FeedTokenDto(existing.Token, $"/feeds/{existing.Token}.ics"));
    }

    private static async Task<IResult> GetFeed(
        string token, CalCronyDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var feedToken = await db.IcsFeedTokens.FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
        if (feedToken is null)
        {
            return Results.NotFound();
        }

        // Include a month of history so recently finished events don't vanish from subscribers.
        var horizon = clock.GetCurrentInstant().Minus(NodaTime.Duration.FromDays(30));
        var events = await db.Events
            .Where(e => e.GuildId == feedToken.GuildId
                        && e.Status != EventStatus.Cancelled
                        && e.StartsAt >= horizon)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(cancellationToken);

        var calendar = new Ical.Net.Calendar();
        calendar.AddProperty("X-WR-CALNAME", "CalCrony events");
        calendar.AddProperty("METHOD", "PUBLISH");

        foreach (var ev in events)
        {
            var start = ev.StartsAt.ToDateTimeUtc();
            calendar.Events.Add(new CalendarEvent
            {
                Uid = $"{ev.Id}@calcrony",
                Summary = ev.Title,
                Description = ev.Description,
                Location = ev.Location,
                DtStart = new CalDateTime(start),
                DtEnd = new CalDateTime(start.AddMinutes(ev.DurationMinutes ?? 60)),
                DtStamp = new CalDateTime(ev.CreatedAt.ToDateTimeUtc()),
            });
        }

        var text = new CalendarSerializer().SerializeToString(calendar);
        return Results.Text(text, "text/calendar; charset=utf-8");
    }
}
