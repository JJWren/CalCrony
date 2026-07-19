using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>Spawns the next occurrence of a series into the current DbContext (caller saves).
/// Used by the scheduler sweep (when a live slot frees) and the skip endpoint. The partial unique
/// index IX_Events_SeriesId_Live backstops concurrent spawns — the losing SaveChanges rolls back
/// whole and converges on the next sweep tick.</summary>
/// <param name="db">The database context.</param>
public sealed class SeriesMaterializer(CalCronyDbContext db)
{
    /// <summary>Adds the next occurrence + its PostEventMessage delivery. Requires
    /// series.NotificationSpecs loaded. Returns null when an end condition ends the series.</summary>
    /// <param name="series">The series row (with notification specs loaded).</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The new occurrence, or null when the series ended instead.</returns>
    public Event? MaterializeNext(EventSeries series, Instant now)
    {
        if (series.Ended)
        {
            return null;
        }

        if (series.MaxOccurrences is int max && series.OccurrenceCount >= max)
        {
            series.Ended = true;
            return null;
        }

        var zone = Mapping.FindZone(series.TimeZone) ?? DateTimeZone.Utc;
        var next = RecurrenceCalculator.NextOccurrence(
            series.Unit, series.Interval, series.MonthlyMode, series.AnchorDate, series.StartTime,
            zone, series.CurrentOccurrenceDate, series.UntilDate, now);
        if (next is null)
        {
            series.Ended = true;
            return null;
        }

        var ev = new Event
        {
            Id = Guid.NewGuid(),
            GuildId = series.GuildId,
            CreatorId = series.CreatorId,
            Title = series.Title,
            Description = series.Description,
            StartsAt = next.Value.Instant,
            TimeZone = series.TimeZone,
            DurationMinutes = series.DurationMinutes,
            ChannelId = series.ChannelId,
            Location = series.Location,
            ImageUrl = series.ImageUrl,
            Status = EventStatus.Scheduled,
            SeriesId = series.Id,
            Series = series,
            CreatedAt = now,
            Options = EventEndpoints.DefaultRsvpOptions(),
            Notifications = [.. series.NotificationSpecs.Select(spec => new EventNotification
            {
                Id = Guid.NewGuid(),
                MinutesBefore = spec.MinutesBefore,
                Message = spec.Message,
                Mentions = spec.Mentions,
                ChannelId = spec.ChannelId,
                SeriesNotificationId = spec.Id,
            })],
        };
        db.Events.Add(ev);
        series.CurrentOccurrenceDate = next.Value.Date;
        series.OccurrenceCount++;

        // Always via the outbox, both caller types — the scheduler has no interaction context and
        // the hardened PostEventMessage handler already covers retries/compensation.
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(),
            Type = DeliveryType.PostEventMessage,
            ChannelId = series.ChannelId,
            PayloadJson = JsonSerializer.Serialize(new PostEventMessagePayload(ev.Id)),
            DueAt = now,
            Status = DeliveryStatus.Pending,
            CreatedAt = now,
        });
        return ev;
    }
}
