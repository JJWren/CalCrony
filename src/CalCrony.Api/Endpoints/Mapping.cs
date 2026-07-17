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
}
