using CalCrony.Api.Data;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Endpoints;

public static class Mapping
{
    public static EventDto ToDto(this Event ev) => new(
        ev.Id,
        ev.GuildId,
        ev.CreatorId,
        ev.Title,
        ev.Description,
        ev.StartsAt.ToDateTimeOffset(),
        ev.TimeZone,
        ev.DurationMinutes,
        ev.ChannelId,
        ev.MessageId,
        ev.Location,
        ev.ImageUrl,
        ev.Status,
        [.. ev.Options.OrderBy(o => o.SortOrder).Select(o => new RsvpOptionDto(o.Id, o.Emote, o.Label, o.SortOrder, o.Capacity))],
        [.. ev.Rsvps.OrderBy(r => r.CreatedAt).Select(r => new RsvpDto(r.UserId, r.OptionId))],
        ev.SeriesId,
        // Summary requires the Series nav loaded; ended series read as one-offs (no 🔁).
        ev.Series is { Ended: false } series ? Services.RecurrenceCalculator.Describe(series) : null);

    public static SeriesDto ToDto(this EventSeries series, Guid? liveEventId) => new(
        series.Id,
        series.GuildId,
        series.CreatorId,
        series.Title,
        series.Unit,
        series.Interval,
        series.MonthlyMode,
        series.TimeZone,
        series.AnchorDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        series.StartTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        series.UntilDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        series.MaxOccurrences,
        series.OccurrenceCount,
        series.Ended,
        liveEventId,
        series.Description,
        series.DurationMinutes,
        series.ChannelId,
        series.Location,
        series.ImageUrl,
        Services.RecurrenceCalculator.Describe(series),
        [.. series.NotificationSpecs.OrderByDescending(n => n.MinutesBefore)
            .Select(n => new SeriesNotificationDto(n.Id, n.MinutesBefore, n.Message, n.Mentions, n.ChannelId))]);

    public static DateTimeZone? FindZone(string? id) =>
        id is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(id);

    /// <summary>Anonymity shaping: on anonymous polls, non-bot viewers get only their OWN vote
    /// rows (so the UI can highlight their picks) while per-option VoteCounts stay complete.
    /// The bot receives everything and hides names in its embed builder.</summary>
    public static PollDto ToDto(this Poll poll, long? viewerUserId, bool viewerIsBot)
    {
        var orderedOptions = poll.IsTimePoll
            ? poll.Options.OrderBy(o => o.SlotAt).ToList()
            : poll.Options.OrderBy(o => o.SortOrder).ToList();

        var votes = poll.Anonymous && !viewerIsBot
            ? poll.Votes.Where(v => v.UserId == viewerUserId).ToList()
            : poll.Votes.ToList();

        return new PollDto(
            poll.Id,
            poll.GuildId,
            poll.CreatorId,
            poll.Question,
            poll.IsTimePoll,
            poll.SingleVote,
            poll.Anonymous,
            poll.AllowUserOptions,
            poll.ChannelId,
            poll.MessageId,
            poll.Status,
            poll.ClosesAt?.ToDateTimeOffset(),
            poll.ClosedAt?.ToDateTimeOffset(),
            poll.TimeZone,
            poll.ConvertedEventId,
            [.. orderedOptions.Select(o => new PollOptionDto(
                o.Id, o.Text, o.SlotAt?.ToDateTimeOffset(), o.AddedByUserId, o.SortOrder,
                poll.Votes.Count(v => v.OptionId == o.Id)))],
            [.. votes.Select(v => new PollVoteDto(v.UserId, v.OptionId))]);
    }
}
