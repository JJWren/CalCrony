using CalCrony.Api.Data;
using CalCrony.Contracts;
using NodaTime;

namespace CalCrony.Api.Endpoints;

/// <summary>Entity-to-DTO projections shared across endpoint groups.</summary>
public static class Mapping
{
    /// <summary>Projects an event with ordered options/RSVPs; the recurrence summary requires the Series navigation loaded.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="channelName">The channel-name snapshot to carry, when the caller looked one up.</param>
    /// <param name="roleNames">Role-name snapshots for the options' roles, when the caller looked
    /// them up; unknown roles render with a null name (id fallback on the consumer side).</param>
    /// <returns>The projected DTO.</returns>
    public static EventDto ToDto(
        this Event ev, string? channelName = null, IReadOnlyDictionary<long, string?>? roleNames = null) => new(
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
        [.. ev.Options.OrderBy(o => o.SortOrder)
            .Select(o => new RsvpOptionDto(
                o.Id, o.Emote, o.Label, o.SortOrder, o.Capacity, o.IsAttending, o.AttendeeRoleId,
                RoleRefs(o.AllowedRoleIds, roleNames),
                o.AttendeeRoleId is { } attendeeRole ? roleNames?.GetValueOrDefault(attendeeRole) : null))],
        [.. ev.Rsvps.OrderBy(r => r.CreatedAt).Select(r => new RsvpDto(r.UserId, r.OptionId, r.Waitlisted))],
        ev.SeriesId,
        // Summary requires the Series nav loaded; ended series read as one-offs (no 🔁).
        ev.Series is { Ended: false } series ? Services.RecurrenceCalculator.Describe(series) : null,
        ev.NativeEventId,
        // Roles live on the options; this mirrors the attending one so pre-v2 consumers still read
        // "the event's role" without walking Options.
        Services.RsvpPolicy.AttendingOption(ev.Options)?.AttendeeRoleId,
        ev.WantsThread,
        ev.ThreadId,
        channelName,
        // Clients get the resolved cutoff instant — relative-vs-absolute is a storage detail.
        Services.RsvpPolicy.EffectiveClose(ev)?.ToDateTimeOffset(),
        // The restriction mirror follows the AttendeeRoleId one: the per-option sets are the
        // truth; this is the common set when every option agrees, null when they differ.
        SharedAllowedRoles(ev.Options, roleNames),
        ev.AllowMultipleRsvps);

    /// <summary>Role ids to references, with whatever names the snapshot holds — minus the roles
    /// the snapshot says are deleted. A tombstone (present in the lookup with a null name) is a
    /// role the restriction no longer gates on, so it is not part of the restriction clients see;
    /// an id the lookup simply doesn't know keeps its place with a null name (id fallback).</summary>
    /// <param name="roleIds">The stored role ids.</param>
    /// <param name="roleNames">Role-name snapshots, or null for none.</param>
    /// <returns>The effective references, in the ids' order.</returns>
    internal static IReadOnlyList<RoleRefDto> RoleRefs(long[] roleIds, IReadOnlyDictionary<long, string?>? roleNames) =>
        [.. EffectiveRoleIds(roleIds, roleNames).Select(id => new RoleRefDto(id, roleNames?.GetValueOrDefault(id)))];

    /// <summary>The stored restriction minus tombstoned roles (see <see cref="RoleRefs"/>).</summary>
    /// <param name="roleIds">The stored role ids.</param>
    /// <param name="roleNames">Role-name snapshots, or null for none.</param>
    /// <returns>The ids still gating.</returns>
    private static IEnumerable<long> EffectiveRoleIds(long[] roleIds, IReadOnlyDictionary<long, string?>? roleNames) =>
        roleIds.Where(id => roleNames is null || !roleNames.TryGetValue(id, out var name) || name is not null);

    /// <summary>The restriction every option shares — empty when none is restricted — or null when
    /// the options disagree (set-wise: order doesn't matter).</summary>
    /// <param name="options">The event's options.</param>
    /// <param name="roleNames">Role-name snapshots, or null for none.</param>
    /// <returns>The common restriction, or null.</returns>
    private static IReadOnlyList<RoleRefDto>? SharedAllowedRoles(
        IEnumerable<RsvpOption> options, IReadOnlyDictionary<long, string?>? roleNames)
    {
        long[]? first = null;
        HashSet<long>? common = null;
        foreach (var option in options)
        {
            var effective = EffectiveRoleIds(option.AllowedRoleIds, roleNames).ToArray();
            if (first is null)
            {
                first = effective;
                common = [.. first];
                continue;
            }

            if (!common!.SetEquals(effective))
            {
                return null;
            }
        }

        return RoleRefs(first ?? [], roleNames);
    }

    /// <summary>Projects a series' schedule, template, progress, and notification specs.</summary>
    /// <param name="series">The series row (with notification specs loaded).</param>
    /// <param name="liveEventId">The live occurrence's event id, when one exists.</param>
    /// <returns>The projected DTO.</returns>
    public static SeriesDto ToDto(this EventSeries series, Guid? liveEventId) => new(
        series.Id,
        series.GuildId,
        series.CreatorId,
        series.Title,
        series.Unit,
        series.Interval,
        series.MonthlyMode,
        series.DaysOfWeek,
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

    /// <summary>Projects a live list registration.</summary>
    /// <param name="list">The live list row.</param>
    /// <returns>The projected DTO.</returns>
    public static LiveListDto ToDto(this LiveList list) => new(
        list.Id, list.GuildId, list.ChannelId, list.MessageId, list.Limit, list.CreatorId);

    /// <summary>Resolves an IANA id to a zone, null when unknown.</summary>
    /// <param name="id">The IANA zone id.</param>
    /// <returns>The zone, or null when the id is unknown.</returns>
    public static DateTimeZone? FindZone(string? id) =>
        id is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(id);

    /// <summary>Anonymity shaping: on anonymous polls, non-bot viewers get only their OWN vote
    /// rows (so the UI can highlight their picks) while per-option VoteCounts stay complete.
    /// The bot receives everything and hides names in its embed builder.</summary>
    /// <param name="poll">The poll.</param>
    /// <param name="viewerUserId">The web caller's Discord id, when a JWT caller.</param>
    /// <param name="viewerIsBot">True when the bot is the caller (sees all votes).</param>
    /// <param name="roleNames">Role-name snapshots for the restriction, when the caller looked them up.</param>
    /// <returns>The projected DTO.</returns>
    public static PollDto ToDto(
        this Poll poll, long? viewerUserId, bool viewerIsBot, IReadOnlyDictionary<long, string?>? roleNames = null)
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
            [.. votes.Select(v => new PollVoteDto(v.UserId, v.OptionId))],
            RoleRefs(poll.AllowedRoleIds, roleNames));
    }
}
