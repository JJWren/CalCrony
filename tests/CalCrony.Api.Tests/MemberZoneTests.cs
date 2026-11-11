using CalCrony.Api.Services;

namespace CalCrony.Api.Tests;

/// <summary>The zone a member's days and hours are read in: theirs, else the server's, else UTC.
/// (That the datetime route answers the same is pinned at route level in FreeSlotsApiTests.)</summary>
public class MemberZoneTests
{
    [Theory]
    [InlineData("Europe/Berlin", "America/Chicago", "Europe/Berlin")]   // the member's own zone wins
    [InlineData(null, "America/Chicago", "America/Chicago")]            // none set: the server's
    [InlineData("Mars/Olympus_Mons", "America/Chicago", "America/Chicago")] // an id that names no zone falls through
    [InlineData(null, null, "UTC")]                                     // a server CalCrony has no row for
    [InlineData(null, "Not/AZone", "UTC")]
    [InlineData("Europe/Berlin", null, "Europe/Berlin")]
    public void Resolves_the_members_zone_then_the_servers_then_utc(string? member, string? guild, string expected)
    {
        Assert.Equal(expected, MemberZone.Resolve(member, guild).Id);
    }
}
