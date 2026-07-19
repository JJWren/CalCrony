using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

public class AttendeeRoleApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 9700;
    private const long ChannelId = 9701;
    private const long CreatorId = 9702;
    private const long RoleA = 777100;
    private const long RoleB = 777200;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Create_with_role_roundtrips_and_web_create_ignores_it()
    {
        var ev = await CreateEventAsync("Role roundtrip", RoleA);
        Assert.Equal(RoleA, ev.AttendeeRoleId);

        await Client.PutAsJsonAsync($"/guilds/{GuildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        var (member, _) = await fixture.LoginAsync(9750, (GuildId, "G", false));
        var create = await member.PostAsJsonAsync($"/guilds/{GuildId}/events",
            new CreateEventRequest(0, "Web role attempt", "in 2 hours", 0, AttendeeRoleId: RoleA));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var webEvent = (await create.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Null(webEvent.AttendeeRoleId);
    }

    [Fact]
    public async Task Going_rsvp_enqueues_grant_for_bot_and_web_callers()
    {
        var ev = await CreateEventAsync("Grant both callers", RoleA);
        var going = GoingOption(ev);

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9751", new RsvpRequest(going.Id)))
            .EnsureSuccessStatusCode();

        var (member, _) = await fixture.LoginAsync(9752, (GuildId, "G", false));
        (await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9752", new RsvpRequest(going.Id)))
            .EnsureSuccessStatusCode();

        var grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Equal(2, grants.Count);
        Assert.All(grants, p =>
        {
            Assert.Equal(GuildId, p.GuildId);
            Assert.Equal(RoleA, p.RoleId);
        });
        Assert.Contains(grants, p => p.UserId == 9751);
        Assert.Contains(grants, p => p.UserId == 9752);
    }

    [Fact]
    public async Task Duplicate_going_put_dedups_the_pending_grant()
    {
        var ev = await CreateEventAsync("Grant dedup", RoleA);
        var going = GoingOption(ev);

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9753", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9753", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();

        Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));
    }

    [Fact]
    public async Task Rapid_toggle_cancels_before_serve_but_revokes_after_serve()
    {
        var ev = await CreateEventAsync("Toggle netting", RoleA);
        var going = GoingOption(ev);
        var maybe = ev.Options.First(o => o.Id != going.Id);

        // Going→Maybe with the grant never served: the pair nets to zero rows.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9754", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9754", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();
        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));
        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));

        // Same toggle with the grant already in flight (served once): the revoke must enqueue.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9754", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Deliveries
                .Where(d => d.Type == DeliveryType.GrantAttendeeRole && d.PayloadJson.Contains(ev.Id.ToString()))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, 1));
        }

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9754", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();
        Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));
        Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
    }

    [Fact]
    public async Task Delete_rsvp_revokes_only_when_it_was_going()
    {
        var ev = await CreateEventAsync("Un-RSVP", RoleA);
        var going = GoingOption(ev);
        var maybe = ev.Options.First(o => o.Id != going.Id);

        // Maybe → delete: never Going, so no role rows at all.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9755", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/9755")).EnsureSuccessStatusCode();
        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));

        // Going (served) → delete: revoke.
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9756", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Deliveries
                .Where(d => d.Type == DeliveryType.GrantAttendeeRole && d.PayloadJson.Contains(ev.Id.ToString()))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, 1));
        }

        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/9756")).EnsureSuccessStatusCode();
        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal(9756, revoke.UserId);
    }

    [Fact]
    public async Task Events_without_a_role_never_enqueue_role_deliveries()
    {
        var ev = await CreateEventAsync("No role", attendeeRoleId: null);
        var going = GoingOption(ev);

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9757", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/9757")).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode();

        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));
        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
    }

    [Fact]
    public async Task Rsvp_on_a_non_live_event_does_not_grant()
    {
        var ev = await CreateEventAsync("Ended RSVP", RoleA);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Ended));
        }

        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9758", new RsvpRequest(GoingOption(ev).Id)))
            .EnsureSuccessStatusCode();
        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));
    }

    [Fact]
    public async Task End_sweep_revokes_each_going_attendee_once()
    {
        var ev = await CreateEventAsync("End sweep", RoleA);
        var going = GoingOption(ev);
        var maybe = ev.Options.First(o => o.Id != going.Id);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9760", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9761", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9762", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(3));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.StartsAt, past)
                    .SetProperty(e => e.Status, EventStatus.Started));
            // Clear the RSVP-time grants so the sweep's fan-out is counted in isolation.
            await db.Deliveries
                .Where(d => d.Type == DeliveryType.GrantAttendeeRole && d.PayloadJson.Contains(ev.Id.ToString()))
                .ExecuteDeleteAsync();
        }

        await SweepAsync();
        await SweepAsync(); // idempotent — the transition fires once

        var revokes = await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        Assert.Equal(2, revokes.Count);
        Assert.Contains(revokes, p => p.UserId == 9760);
        Assert.Contains(revokes, p => p.UserId == 9761);
        Assert.DoesNotContain(revokes, p => p.UserId == 9762);
    }

    [Fact]
    public async Task Delete_event_enqueues_revokes_before_the_rsvps_cascade_away()
    {
        var ev = await CreateEventAsync("Delete revoke", RoleA);
        var going = GoingOption(ev);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9763", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        await MarkGrantsServedAsync(ev.Id);

        (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode();

        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal(9763, revoke.UserId);
        Assert.Equal(RoleA, revoke.RoleId);
    }

    [Fact]
    public async Task Skip_revokes_and_the_spawned_occurrence_inherits_the_series_role()
    {
        var ev = await CreateEventAsync("Skip inherit", RoleA, recurring: true);
        var going = GoingOption(ev);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9764", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        await MarkGrantsServedAsync(ev.Id);

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(RoleA, next.AttendeeRoleId);

        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal(9764, revoke.UserId);
    }

    [Fact]
    public async Task Patch_cancel_revokes_all_going_attendees()
    {
        var ev = await CreateEventAsync("Patch cancel", RoleA);
        var going = GoingOption(ev);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9765", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        await MarkGrantsServedAsync(ev.Id);

        (await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, Status: EventStatus.Cancelled))).EnsureSuccessStatusCode();

        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal(9765, revoke.UserId);
        Assert.Equal(RoleA, revoke.RoleId);
    }

    [Fact]
    public async Task Role_change_resyncs_and_clear_revokes()
    {
        var ev = await CreateEventAsync("Role resync", RoleA);
        var going = GoingOption(ev);
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/9766", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        await MarkGrantsServedAsync(ev.Id);

        // A → B: revoke-all A + grant-all B for the current Going set.
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, AttendeeRoleId: RoleB))).EnsureSuccessStatusCode();
        var afterChange = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(RoleB, afterChange!.AttendeeRoleId);
        var revokesA = await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        Assert.Contains(revokesA, p => p.RoleId == RoleA && p.UserId == 9766);
        var grantsB = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Contains(grantsB, p => p.RoleId == RoleB && p.UserId == 9766);

        // Clear: revoke-all B, no new grants.
        await MarkGrantsServedAsync(ev.Id);
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, ClearAttendeeRole: true))).EnsureSuccessStatusCode();
        Assert.Null((await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.AttendeeRoleId);
        var revokesB = await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        Assert.Contains(revokesB, p => p.RoleId == RoleB && p.UserId == 9766);
    }

    [Fact]
    public async Task Set_and_clear_conflict_and_web_selection_is_rejected()
    {
        var ev = await CreateEventAsync("Role validation", RoleA);

        var both = await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, AttendeeRoleId: RoleB, ClearAttendeeRole: true));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var creatorWeb = await fixture.LoginAsync(CreatorId, (GuildId, "G", true));
        var webSet = await creatorWeb.Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(0, AttendeeRoleId: RoleB));
        Assert.Equal(HttpStatusCode.BadRequest, webSet.StatusCode);

        var webClear = await creatorWeb.Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(0, ClearAttendeeRole: true));
        webClear.EnsureSuccessStatusCode();
        Assert.Null((await webClear.Content.ReadFromJsonAsync<EventDto>())!.AttendeeRoleId);
    }

    [Fact]
    public async Task Series_scope_role_edit_updates_the_template_and_occurrence_scope_does_not()
    {
        var ev = await CreateEventAsync("Series role edit", RoleA, recurring: true);

        (await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, Scope: EditScope.Occurrence, AttendeeRoleId: RoleB)))
            .EnsureSuccessStatusCode();
        Assert.Equal(RoleA, await SeriesRoleAsync(ev.SeriesId!.Value));

        (await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, Scope: EditScope.Series, ClearAttendeeRole: true)))
            .EnsureSuccessStatusCode();
        Assert.Null(await SeriesRoleAsync(ev.SeriesId!.Value));
    }

    private static RsvpOptionDto GoingOption(EventDto ev) => ev.Options.OrderBy(o => o.SortOrder).First();

    private async Task<EventDto> CreateEventAsync(string title, long? attendeeRoleId, bool recurring = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, title, "in 2 hours", ChannelId,
            Recurrence: recurring ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null,
            AttendeeRoleId: attendeeRoleId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<List<AttendeeRolePayload>> RoleDeliveriesAsync(Guid eventId, DeliveryType type)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return [.. rows.Select(d => System.Text.Json.JsonSerializer.Deserialize<AttendeeRolePayload>(d.PayloadJson)!)];
    }

    /// <summary>Marks the event's pending grants as served (Attempts = 1) so later opposite-type
    /// enqueues can't cancel them — isolating the fan-out under test.</summary>
    private async Task MarkGrantsServedAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Deliveries
            .Where(d => d.Type == DeliveryType.GrantAttendeeRole && d.PayloadJson.Contains(eventId.ToString()))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, 1));
    }

    private async Task<long?> SeriesRoleAsync(Guid seriesId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return (await db.EventSeries.AsNoTracking().FirstAsync(s => s.Id == seriesId)).AttendeeRoleId;
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }
}
