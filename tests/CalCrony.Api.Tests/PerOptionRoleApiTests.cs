using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

/// <summary>RSVP v2 §3.6: an attendee role per RSVP option, so one event hands out Tank/Healer/DPS
/// (issue #125). The event-level AttendeeRoleId survives as shorthand for the ATTENDING option's
/// role — the same relationship AttendeeLimit has with that option's capacity.</summary>
public class PerOptionRoleApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 11300;
    private const long ChannelId = 11301;
    private const long CreatorId = 11302;
    private const long TankRole = 993100;
    private const long HealerRole = 993200;
    private const long DpsRole = 993300;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Each_option_carries_its_own_role_and_rsvps_earn_the_one_they_pick()
    {
        var ev = await CreateRaidAsync("Role per option");
        var (tank, healer, _) = Roles(ev);

        // The DTO exposes the roles per option, and mirrors the attending one for pre-v2 clients.
        Assert.Equal([TankRole, HealerRole, DpsRole], ev.RoleGrantingOptions.Select(o => o.AttendeeRoleId));
        Assert.Equal(TankRole, ev.AttendeeRoleId);

        await RsvpAsync(ev.Id, 601, tank.Id);
        await RsvpAsync(ev.Id, 602, healer.Id);

        var grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, g => g.UserId == 601 && g.RoleId == TankRole);
        // The headline: 602 earns HEALER, not the attending option's role.
        Assert.Contains(grants, g => g.UserId == 602 && g.RoleId == HealerRole);
    }

    [Fact]
    public async Task Switching_options_swaps_one_role_for_the_other_and_leaving_hands_it_back()
    {
        var ev = await CreateRaidAsync("Role swap");
        var (tank, healer, _) = Roles(ev);

        await RsvpAsync(ev.Id, 611, tank.Id);
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole); // the bot delivered the Tank role

        await RsvpAsync(ev.Id, 611, healer.Id);
        Assert.Contains(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole),
            r => r.UserId == 611 && r.RoleId == TankRole);
        Assert.Contains(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole),
            g => g.UserId == 611 && g.RoleId == HealerRole);

        // Un-RSVPing gives back whatever they were holding at the time — Healer, not Tank.
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        await MarkServedAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/611")).EnsureSuccessStatusCode();

        var finalRevoke = Assert.Single(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole),
            r => r.Status == DeliveryStatus.Pending);
        Assert.Equal((611L, HealerRole), (finalRevoke.UserId, finalRevoke.RoleId));
    }

    [Fact]
    public async Task Ending_the_event_revokes_every_options_role_from_its_own_members()
    {
        var ev = await CreateRaidAsync("Raid over");
        var (tank, healer, dps) = Roles(ev);
        await RsvpAsync(ev.Id, 621, tank.Id);
        await RsvpAsync(ev.Id, 622, healer.Id);
        await RsvpAsync(ev.Id, 623, dps.Id);
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended))).EnsureSuccessStatusCode();

        var revokes = await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        Assert.Equal(3, revokes.Count);
        Assert.Contains(revokes, r => r.UserId == 621 && r.RoleId == TankRole);
        Assert.Contains(revokes, r => r.UserId == 622 && r.RoleId == HealerRole);
        Assert.Contains(revokes, r => r.UserId == 623 && r.RoleId == DpsRole);
    }

    [Fact]
    public async Task Re_roling_one_option_moves_only_that_options_members()
    {
        var ev = await CreateRaidAsync("Re-role healers");
        var (tank, healer, _) = Roles(ev);
        await RsvpAsync(ev.Id, 631, tank.Id);
        await RsvpAsync(ev.Id, 632, healer.Id);
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        const long NewHealerRole = 993201;
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, RsvpOptions:
        [
            new RsvpOptionSpec("🛡️", "Tank", 2, IsAttending: true, AttendeeRoleId: TankRole),
            new RsvpOptionSpec("💚", "Healer", 2, AttendeeRoleId: NewHealerRole),
            new RsvpOptionSpec("⚔️", "DPS", 6, AttendeeRoleId: DpsRole),
        ]))).EnsureSuccessStatusCode();

        // The healer swaps roles; the tank, whose option was untouched, gets no delivery at all.
        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal((632L, HealerRole), (revoke.UserId, revoke.RoleId));
        Assert.Contains(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole),
            g => g.UserId == 632 && g.RoleId == NewHealerRole);
        Assert.DoesNotContain(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole), r => r.UserId == 631);
    }

    [Fact]
    public async Task The_event_level_role_is_shorthand_for_the_attending_option()
    {
        // No options given: the shorthand caps the default "Going" option, exactly like AttendeeLimit.
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Shorthand", "in 3 hours", ChannelId, AttendeeRoleId: TankRole));
        Assert.Equal(TankRole, ev.AttendingOption!.AttendeeRoleId);
        Assert.Equal(TankRole, ev.AttendeeRoleId);
        Assert.All(ev.Options.Where(o => !o.IsAttending), o => Assert.Null(o.AttendeeRoleId));

        // A role-only edit re-roles that option in place…
        var rerolled = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AttendeeRoleId: HealerRole)));
        Assert.Equal(HealerRole, rerolled.AttendingOption!.AttendeeRoleId);

        // …and clearing removes it.
        var cleared = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearAttendeeRole: true)));
        Assert.Null(cleared.AttendingOption!.AttendeeRoleId);
        Assert.Null(cleared.AttendeeRoleId);
        Assert.Empty(cleared.RoleGrantingOptions);
    }

    [Fact]
    public async Task Setting_the_attending_options_role_twice_is_rejected()
    {
        // Same rule the attendee limit has: the shorthand and the spec are one setting.
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Role given twice", "in 3 hours", ChannelId,
            AttendeeRoleId: TankRole,
            RsvpOptions: [new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AttendeeRoleId: HealerRole)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not both", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task A_role_on_a_non_attending_option_still_only_reaches_seated_users()
    {
        // Healer is capped at 1, so the second healer queues — and a queued RSVP holds no role
        // until a seat frees, exactly like the attending option's waitlist.
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Capped healer", "in 3 hours", ChannelId, RsvpOptions:
            [
                new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AttendeeRoleId: TankRole),
                new RsvpOptionSpec("💚", "Healer", 1, AttendeeRoleId: HealerRole),
            ]));
        var healer = ev.Options.Single(o => o.Label == "Healer");

        await RsvpAsync(ev.Id, 641, healer.Id);
        // Non-attending options have no waitlist — they reject at capacity (RSVP v1 rule).
        var full = await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/642", new RsvpRequest(healer.Id));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);

        var grant = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));
        Assert.Equal((641L, HealerRole), (grant.UserId, grant.RoleId));
    }

    [Fact]
    public async Task Series_occurrences_spawn_with_the_whole_per_option_role_set()
    {
        var created = await CreateAsync(new CreateEventRequest(
            CreatorId, "Weekly raid", "in 3 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week),
            RsvpOptions:
            [
                new RsvpOptionSpec("🛡️", "Tank", 2, IsAttending: true, AttendeeRoleId: TankRole),
                new RsvpOptionSpec("💚", "Healer", 2, AttendeeRoleId: HealerRole),
            ]));

        // The roles ride the series' option template (where capacities already live), so the next
        // occurrence the materializer spawns starts with all of them.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var series = await db.EventSeries.AsNoTracking().FirstAsync(s => s.Id == created.SeriesId);
        var template = RsvpPolicy.OptionsFromTemplate(series.RsvpOptionsJson);

        Assert.Equal(
            [("Tank", (long?)TankRole), ("Healer", HealerRole)],
            template.OrderBy(o => o.SortOrder).Select(o => (o.Label, o.AttendeeRoleId)));
    }

    [Fact]
    public async Task A_combined_role_and_limit_edit_applies_both_not_just_the_role()
    {
        // The two shorthands are independent settings; create takes them together, so edit must
        // too. They used to sit in one if/else-if chain, where the role branch swallowed the limit.
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Both shorthands", "in 3 hours", ChannelId,
            AttendeeRoleId: TankRole, AttendeeLimit: 2));

        var edited = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AttendeeRoleId: HealerRole, AttendeeLimit: 5)));

        Assert.Equal(HealerRole, edited.AttendingOption!.AttendeeRoleId);
        Assert.Equal(5, edited.AttendingOption.Capacity);

        // Clearing both at once is the same story in reverse.
        var cleared = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearAttendeeRole: true, ClearAttendeeLimit: true)));
        Assert.Null(cleared.AttendingOption!.AttendeeRoleId);
        Assert.Null(cleared.AttendingOption.Capacity);
    }

    [Fact]
    public async Task A_combined_series_scoped_role_and_limit_edit_updates_both_on_the_template()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Both on the template", "in 3 hours", ChannelId,
            AttendeeRoleId: TankRole, AttendeeLimit: 2,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));

        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Series, AttendeeRoleId: HealerRole, AttendeeLimit: 5)))
            .EnsureSuccessStatusCode();

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(HealerRole, next.AttendingOption!.AttendeeRoleId);
        Assert.Equal(5, next.AttendingOption.Capacity);
    }

    [Fact]
    public async Task Clearing_while_moving_the_attending_flag_clears_the_option_the_caller_named()
    {
        // The clear names the role the caller was looking at — Tank's, the option attending BEFORE
        // the edit — even though this same submission moves the flag to Healer.
        var ev = await CreateRaidAsync("Clear and move");
        var (tank, healer, _) = Roles(ev);
        await RsvpAsync(ev.Id, 661, tank.Id);
        await RsvpAsync(ev.Id, 662, healer.Id);
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        var edited = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearAttendeeRole: true, RsvpOptions:
            [
                new RsvpOptionSpec("🛡️", "Tank", 2),
                new RsvpOptionSpec("💚", "Healer", 2, IsAttending: true, AttendeeRoleId: HealerRole),
                new RsvpOptionSpec("⚔️", "DPS", 6, AttendeeRoleId: DpsRole),
            ])));

        Assert.Null(edited.Options.Single(o => o.Label == "Tank").AttendeeRoleId);
        Assert.Equal(HealerRole, edited.Options.Single(o => o.Label == "Healer").AttendeeRoleId);

        // Tank's holder loses the role that was actually cleared; the healer keeps theirs.
        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal((661L, TankRole), (revoke.UserId, revoke.RoleId));
        Assert.DoesNotContain(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole), r => r.UserId == 662);
    }

    [Fact]
    public async Task A_clear_alongside_replacement_options_is_honoured_not_dropped()
    {
        var ev = await CreateRaidAsync("Clear with options");
        await RsvpAsync(ev.Id, 671, Roles(ev).Tank.Id);
        // Served first, so the clear produces a real revoke rather than netting the still-pending
        // grant away (both are correct outcomes; this pins the one with a delivery to inspect).
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        // Same option set, Tank's role simply left unstated plus the clear flag.
        var edited = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearAttendeeRole: true, RsvpOptions:
            [
                new RsvpOptionSpec("🛡️", "Tank", 2, IsAttending: true),
                new RsvpOptionSpec("💚", "Healer", 2, AttendeeRoleId: HealerRole),
                new RsvpOptionSpec("⚔️", "DPS", 6, AttendeeRoleId: DpsRole),
            ])));

        Assert.Null(edited.AttendingOption!.AttendeeRoleId);
        Assert.Contains(
            await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole),
            r => r.UserId == 671 && r.RoleId == TankRole);
    }

    [Fact]
    public async Task Giving_and_clearing_the_same_options_role_in_one_edit_is_rejected()
    {
        var ev = await CreateRaidAsync("Contradictory clear");

        var response = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearAttendeeRole: true, RsvpOptions:
            [new RsvpOptionSpec("🛡️", "Tank", 2, IsAttending: true, AttendeeRoleId: HealerRole)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("choose one", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    // ---------- helpers ----------

    /// <summary>Tank (attending) / Healer / DPS, each with its own role — the shape §3.6 exists for.</summary>
    private async Task<EventDto> CreateRaidAsync(string title) =>
        await CreateAsync(new CreateEventRequest(
            CreatorId, title, "in 3 hours", ChannelId, RsvpOptions:
            [
                new RsvpOptionSpec("🛡️", "Tank", 2, IsAttending: true, AttendeeRoleId: TankRole),
                new RsvpOptionSpec("💚", "Healer", 2, AttendeeRoleId: HealerRole),
                new RsvpOptionSpec("⚔️", "DPS", 6, AttendeeRoleId: DpsRole),
            ]));

    private static (RsvpOptionDto Tank, RsvpOptionDto Healer, RsvpOptionDto Dps) Roles(EventDto ev) => (
        ev.Options.Single(o => o.Label == "Tank"),
        ev.Options.Single(o => o.Label == "Healer"),
        ev.Options.Single(o => o.Label == "DPS"));

    private async Task<EventDto> CreateAsync(CreateEventRequest request)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> RsvpAsync(Guid eventId, long userId, Guid optionId) =>
        await ReadAsync<EventDto>(
            await Client.PutAsJsonAsync($"/events/{eventId}/rsvps/{userId}", new RsvpRequest(optionId)));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Role deliveries for one event, with the row status kept so a test can tell a
    /// freshly enqueued revoke from one an earlier step already settled.</summary>
    private async Task<List<(long UserId, long RoleId, DeliveryStatus Status)>> RoleDeliveriesAsync(
        Guid eventId, DeliveryType type)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return
        [
            .. rows.Select(d =>
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<AttendeeRolePayload>(d.PayloadJson)!;
                return (payload.UserId, payload.RoleId, d.Status);
            }),
        ];
    }

    /// <summary>Settles pending deliveries of one type the way a successful bot round would, so a
    /// later opposite enqueue can't coalesce them away.</summary>
    private async Task MarkServedAsync(Guid eventId, DeliveryType type)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Deliveries
            .Where(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeliveryStatus.Sent)
                .SetProperty(d => d.Attempts, 1));
    }
}
