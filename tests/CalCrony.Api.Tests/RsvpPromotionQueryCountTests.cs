using System.Net;
using System.Net.Http.Json;
using CalCrony.Contracts;

namespace CalCrony.Api.Tests;

/// <summary>Waitlist promotion's outbox cost (issue #131). The behavioural rules live in
/// <see cref="RsvpV1ApiTests"/>; this class guards the shape of the work instead.</summary>
public class RsvpPromotionQueryCountTests(SqlCountingApiFixture fixture) : IClassFixture<SqlCountingApiFixture>
{
    private const long GuildId = 11200;
    private const long ChannelId = 11201;
    private const long CreatorId = 11202;
    private const long AttendeeRoleId = 888400;

    private HttpClient Client => fixture.Client;

    /// <summary>The promotion pass batch-loads its role and thread coalescing state once, so a
    /// limit raise no longer issues two lookups per promoted user. Comparing two identically
    /// shaped events — one seating a queue of 2, one a queue of 6 — pins that directly and stays
    /// immune to the constant number of outbox lookups the rest of the edit does: before the
    /// batching the larger raise cost eight more, after it costs exactly the same.</summary>
    [Fact]
    public async Task Promotion_outbox_lookups_dont_grow_with_the_waitlist()
    {
        var small = await CountRaiseLookupsAsync("Promote two", 700_000, queued: 2);
        var large = await CountRaiseLookupsAsync("Promote six", 800_000, queued: 6);

        Assert.True(small > 0, "the counting interceptor never saw an outbox lookup");
        Assert.Equal(small, large);
    }

    /// <summary>The per-option role diff batch-loads its coalescing state once too. Re-roling an
    /// option is many-roles-many-users — every seated member of that option swaps role — so a
    /// per-user lookup here would reintroduce exactly what the promotion path stopped doing, and
    /// under the event's FOR UPDATE lock. Two identically shaped re-roles, 2 seated vs 6, must
    /// cost the same.</summary>
    [Fact]
    public async Task Role_diff_lookups_dont_grow_with_the_number_of_re_roled_members()
    {
        var small = await CountRerollLookupsAsync("Re-role two", 900_000, seated: 2);
        var large = await CountRerollLookupsAsync("Re-role six", 910_000, seated: 6);

        Assert.True(small > 0, "the counting interceptor never saw an outbox lookup");
        Assert.Equal(small, large);
    }

    /// <summary>Seats <paramref name="seated"/> users on a role-bearing option, then counts the
    /// outbox lookups the edit that re-roles that option issues (revoke old + grant new, for
    /// every seated member).</summary>
    /// <param name="title">The event title.</param>
    /// <param name="firstUserId">The first of the contiguous user ids to RSVP.</param>
    /// <param name="seated">How many users hold the option's role before the re-role.</param>
    /// <returns>The number of Deliveries SELECTs the re-role issued.</returns>
    private async Task<int> CountRerollLookupsAsync(string title, long firstUserId, int seated)
    {
        const long OldRole = 994100;
        const long NewRole = 994200;
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 3 hours", ChannelId,
            RsvpOptions: [new RsvpOptionSpec("🛡️", "Raider", IsAttending: true, AttendeeRoleId: OldRole)]));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        var raider = ev.AttendingOption!;

        for (var i = 0; i < seated; i++)
        {
            (await Client.PutAsJsonAsync(
                    $"/events/{ev.Id}/rsvps/{firstUserId + i}", new RsvpRequest(raider.Id)))
                .EnsureSuccessStatusCode();
        }

        fixture.Counter.Start();
        var rerolled = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, RsvpOptions:
            [new RsvpOptionSpec("🛡️", "Raider", IsAttending: true, AttendeeRoleId: NewRole)]));
        var lookups = fixture.Counter.Stop();

        rerolled.EnsureSuccessStatusCode();
        var dto = (await rerolled.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(NewRole, dto.AttendingOption!.AttendeeRoleId); // the counted request really re-roled
        return lookups;
    }

    /// <summary>Builds a role- and thread-bearing event with one seat taken and
    /// <paramref name="queued"/> users waiting, then counts the outbox lookups the limit raise
    /// that seats the whole queue issues.</summary>
    /// <param name="title">The event title.</param>
    /// <param name="firstUserId">The first of the contiguous user ids to RSVP.</param>
    /// <param name="queued">How many users wait behind the single seat.</param>
    /// <returns>The number of Deliveries SELECTs the raise issued.</returns>
    private async Task<int> CountRaiseLookupsAsync(string title, long firstUserId, int queued)
    {
        var create = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 3 hours", ChannelId,
            AttendeeLimit: 1, AttendeeRoleId: AttendeeRoleId, WantsThread: true));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ev = (await create.Content.ReadFromJsonAsync<EventDto>())!;

        // A live thread as well as a role, so BOTH per-user lookups are on the promotion path.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/thread", new SetThreadRequest(firstUserId)))
            .EnsureSuccessStatusCode();
        var going = ev.AttendingOption!;

        for (var i = 0; i <= queued; i++) // the seat taker, then the queue behind them
        {
            (await Client.PutAsJsonAsync(
                    $"/events/{ev.Id}/rsvps/{firstUserId + i}", new RsvpRequest(going.Id)))
                .EnsureSuccessStatusCode();
        }

        fixture.Counter.Start();
        var raised = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AttendeeLimit: 1 + queued));
        var lookups = fixture.Counter.Stop();

        raised.EnsureSuccessStatusCode();
        var dto = (await raised.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Empty(dto.Waitlist); // the counted request really did seat the whole queue
        return lookups;
    }
}
