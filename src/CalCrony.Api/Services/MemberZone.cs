using CalCrony.Api.Endpoints;
using NodaTime;

namespace CalCrony.Api.Services;

/// <summary>The zone a member's days and hours are read in: their own timezone, else the server's,
/// else UTC — the rule natural-language times already parse by (the datetime route and
/// <see cref="EventEndpoints.ResolveZoneAsync"/> state it inline). This pure form exists for
/// callers that resolve many members from one query.</summary>
public static class MemberZone
{
    /// <summary>Resolves a member's zone from the two stored ids. An id that is missing or no
    /// longer names a zone falls through to the next.</summary>
    /// <param name="memberTimeZone">The member's own IANA zone id, when they set one.</param>
    /// <param name="guildTimeZone">The server's IANA zone id, when the server is known.</param>
    /// <returns>The member's zone, else the server's, else UTC.</returns>
    public static DateTimeZone Resolve(string? memberTimeZone, string? guildTimeZone) =>
        Mapping.FindZone(memberTimeZone) ?? Mapping.FindZone(guildTimeZone) ?? DateTimeZone.Utc;
}
