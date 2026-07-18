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
        [.. ev.Rsvps.OrderBy(r => r.CreatedAt).Select(r => new RsvpDto(r.UserId, r.OptionId))]);

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
