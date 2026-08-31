using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>RSVP v1: custom options, attendee limits, waitlist promotion, and the close-early
/// cutoff (issue #120).</summary>
public class RsvpV1ApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const long GuildId = 11100;
    private const long ChannelId = 11101;
    private const long CreatorId = 11102;

    private HttpClient Client => fixture.Client;

    // ---------- Custom options ----------

    [Fact]
    public async Task Custom_options_replace_the_defaults_and_first_is_attending()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Raid signup", "in 3 hours", ChannelId,
            RsvpOptions:
            [
                new RsvpOptionSpec("⚔️", "Raider", 10),
                new RsvpOptionSpec("🛡️", "Standby"),
                new RsvpOptionSpec("❌", "Out"),
            ]));

        Assert.Collection(
            ev.Options,
            o =>
            {
                Assert.Equal(("⚔️", "Raider", 10, true), (o.Emote, o.Label, o.Capacity, o.IsAttending));
                Assert.Equal(0, o.SortOrder);
            },
            o => Assert.Equal(("🛡️", "Standby", false), (o.Emote, o.Label, o.IsAttending)),
            o => Assert.Equal(("❌", "Out", false), (o.Emote, o.Label, o.IsAttending)));
        Assert.Equal("Raider", ev.AttendingOption!.Label);
    }

    [Fact]
    public async Task Explicit_attending_flag_overrides_the_first_option_default()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Flagged attending", "in 3 hours", ChannelId,
            RsvpOptions:
            [
                new RsvpOptionSpec("❌", "Out"),
                new RsvpOptionSpec("🍕", "In", IsAttending: true),
            ]));

        Assert.Equal("In", ev.AttendingOption!.Label);
        Assert.False(ev.Options.Single(o => o.Label == "Out").IsAttending);
    }

    [Fact]
    public async Task Unspecified_options_get_the_default_set_with_going_attending()
    {
        var ev = await CreateAsync(new CreateEventRequest(CreatorId, "Defaults", "in 3 hours", ChannelId));

        Assert.Equal(["Going", "Not going", "Maybe"], ev.Options.Select(o => o.Label));
        Assert.Equal("Going", ev.AttendingOption!.Label);
        Assert.Null(ev.RsvpClosesAtUtc);
    }

    [Theory]
    [InlineData("dup labels")]
    [InlineData("two attending")]
    [InlineData("capacity conflict")]
    [InlineData("blank label")]
    public async Task Invalid_option_specs_are_rejected(string kind)
    {
        List<RsvpOptionSpec> specs = kind switch
        {
            "dup labels" => [new("✅", "Going"), new("❌", "going")],
            "two attending" => [new("✅", "A", IsAttending: true), new("❌", "B", IsAttending: true)],
            "capacity conflict" => [new("✅", "Going", 5)],
            _ => [new("✅", "  ")],
        };
        int? limit = kind == "capacity conflict" ? 6 : null;

        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Bad options", "in 3 hours", ChannelId, RsvpOptions: specs, AttendeeLimit: limit));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Attendee limit ----------

    [Fact]
    public async Task Attendee_limit_caps_the_default_going_option()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Capped defaults", "in 3 hours", ChannelId, AttendeeLimit: 2));

        Assert.Equal(2, ev.AttendingOption!.Capacity);
        Assert.Null(ev.Options.Single(o => o.Label == "Maybe").Capacity);
    }

    // ---------- Waitlist ----------

    [Fact]
    public async Task Rsvps_past_capacity_queue_in_order_and_dont_take_seats()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Waitlist basics", "in 3 hours", ChannelId, AttendeeLimit: 2));
        var going = ev.AttendingOption!;

        await RsvpAsync(ev.Id, 501, going.Id);
        await RsvpAsync(ev.Id, 502, going.Id);
        var third = await RsvpAsync(ev.Id, 503, going.Id);
        var fourth = await RsvpAsync(ev.Id, 504, going.Id);

        Assert.Equal(2, fourth.SeatedCount(going.Id));
        Assert.Equal([503L, 504L], fourth.Waitlist.Select(r => r.UserId));
        Assert.True(third.Rsvps.Single(r => r.UserId == 503).Waitlisted);
    }

    [Fact]
    public async Task Full_non_attending_options_still_reject_with_409()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Full decline", "in 3 hours", ChannelId,
            RsvpOptions: [new RsvpOptionSpec("✅", "In"), new RsvpOptionSpec("❌", "Out", Capacity: 1)]));
        var outOption = ev.Options.Single(o => o.Label == "Out");

        await RsvpAsync(ev.Id, 511, outOption.Id);
        var full = await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/512", new RsvpRequest(outOption.Id));

        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
    }

    [Fact]
    public async Task Dropping_an_attendee_promotes_the_first_waitlisted_user_with_a_ping()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Promotion on drop", "in 3 hours", ChannelId, AttendeeLimit: 1));
        var going = ev.AttendingOption!;

        await RsvpAsync(ev.Id, 521, going.Id);
        await RsvpAsync(ev.Id, 522, going.Id); // #1 on the waitlist
        await RsvpAsync(ev.Id, 523, going.Id); // #2

        var after = await ReadAsync<EventDto>(await Client.DeleteAsync($"/events/{ev.Id}/rsvps/521"));

        // 522 took the freed seat; 523 stays queued.
        Assert.False(after.Rsvps.Single(r => r.UserId == 522).Waitlisted);
        Assert.Equal([523L], after.Waitlist.Select(r => r.UserId));

        var ping = Assert.Single(await PromotionsAsync(ev.Id));
        Assert.Equal(522, ping.UserId);
        Assert.Equal("Promotion on drop", ping.Title);
    }

    [Fact]
    public async Task Switching_off_the_attending_option_promotes_too()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Promotion on switch", "in 3 hours", ChannelId, AttendeeLimit: 1));
        var going = ev.AttendingOption!;
        var maybe = ev.Options.Single(o => o.Label == "Maybe");

        await RsvpAsync(ev.Id, 531, going.Id);
        await RsvpAsync(ev.Id, 532, going.Id); // waitlisted

        var after = await RsvpAsync(ev.Id, 531, maybe.Id);

        Assert.False(after.Rsvps.Single(r => r.UserId == 532).Waitlisted);
        Assert.Empty(after.Waitlist);
        Assert.Equal(532, Assert.Single(await PromotionsAsync(ev.Id)).UserId);
    }

    [Fact]
    public async Task A_waitlisted_withdrawal_shortens_the_queue_without_promoting()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Waitlist withdrawal", "in 3 hours", ChannelId, AttendeeLimit: 1));
        var going = ev.AttendingOption!;

        await RsvpAsync(ev.Id, 541, going.Id);
        await RsvpAsync(ev.Id, 542, going.Id); // waitlisted

        var after = await ReadAsync<EventDto>(await Client.DeleteAsync($"/events/{ev.Id}/rsvps/542"));

        Assert.Empty(after.Waitlist);
        Assert.Empty(await PromotionsAsync(ev.Id));
    }

    [Fact]
    public async Task Raising_the_limit_seats_waitlisted_users_in_order_and_clearing_seats_all()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Limit raise", "in 3 hours", ChannelId, AttendeeLimit: 1));
        var going = ev.AttendingOption!;

        await RsvpAsync(ev.Id, 551, going.Id);
        await RsvpAsync(ev.Id, 552, going.Id);
        await RsvpAsync(ev.Id, 553, going.Id);
        await RsvpAsync(ev.Id, 554, going.Id);

        // 1 → 3: the first two in the queue are seated, the third keeps waiting.
        var raised = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AttendeeLimit: 3)));
        Assert.Equal(3, raised.SeatedCount(going.Id));
        Assert.Equal([554L], raised.Waitlist.Select(r => r.UserId));
        // Both promotions are enqueued in one transaction, so assert membership, not order.
        Assert.Equal([552L, 553L], (await PromotionsAsync(ev.Id)).Select(p => p.UserId).Order());

        // Clearing the limit seats everyone.
        var cleared = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearAttendeeLimit: true)));
        Assert.Null(cleared.AttendingOption!.Capacity);
        Assert.Empty(cleared.Waitlist);
    }

    [Fact]
    public async Task Waitlisted_users_earn_the_attendee_role_on_promotion_not_on_joining()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Waitlist role", "in 3 hours", ChannelId,
            AttendeeLimit: 1, AttendeeRoleId: 888100));
        var going = ev.AttendingOption!;

        await RsvpAsync(ev.Id, 561, going.Id);
        await RsvpAsync(ev.Id, 562, going.Id); // waitlisted — no grant yet

        var grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Equal([561L], grants.Select(g => g.UserId));

        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/561")).EnsureSuccessStatusCode();

        grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Contains(grants, g => g.UserId == 562);
    }

    // ---------- Option edits ----------

    [Fact]
    public async Task Option_edits_match_by_label_keep_rsvps_and_protect_options_in_use()
    {
        var ev = await CreateAsync(new CreateEventRequest(CreatorId, "Option edits", "in 3 hours", ChannelId));
        var going = ev.AttendingOption!;
        var maybe = ev.Options.Single(o => o.Label == "Maybe");
        await RsvpAsync(ev.Id, 571, going.Id);
        await RsvpAsync(ev.Id, 572, maybe.Id);

        // Dropping an option with RSVPs is a 409…
        var dropInUse = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, RsvpOptions: [new RsvpOptionSpec("✅", "Going"), new RsvpOptionSpec("❌", "Not going")]));
        Assert.Equal(HttpStatusCode.Conflict, dropInUse.StatusCode);

        // …while re-shaping around kept labels updates emotes/capacity and keeps every RSVP.
        var edited = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, RsvpOptions:
            [
                new RsvpOptionSpec("🎮", "Going", 5),
                new RsvpOptionSpec("🤔", "Maybe"),
                new RsvpOptionSpec("🆕", "Late arrival"),
            ])));

        Assert.Equal(["Going", "Maybe", "Late arrival"], edited.Options.Select(o => o.Label));
        Assert.Equal("🎮", edited.AttendingOption!.Emote);
        Assert.Equal(5, edited.AttendingOption.Capacity);
        Assert.Equal(going.Id, edited.AttendingOption.Id); // same row — RSVPs untouched
        Assert.Equal(2, edited.Rsvps.Count);
    }

    [Fact]
    public async Task Moving_the_attending_flag_resyncs_the_role_and_seats_the_old_waitlist()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Attending move", "in 3 hours", ChannelId,
            RsvpOptions: [new RsvpOptionSpec("🍕", "In", 1), new RsvpOptionSpec("🥗", "Salad only")],
            AttendeeRoleId: 888200));
        var inOption = ev.AttendingOption!;
        var salad = ev.Options.Single(o => o.Label == "Salad only");
        await RsvpAsync(ev.Id, 581, inOption.Id); // seated + role grant
        await RsvpAsync(ev.Id, 582, inOption.Id); // waitlisted
        await RsvpAsync(ev.Id, 583, salad.Id);
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        var moved = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(CreatorId, RsvpOptions:
            [
                new RsvpOptionSpec("🍕", "In", 1),
                new RsvpOptionSpec("🥗", "Salad only", IsAttending: true),
            ])));

        // The flag moved; the old option's queue is seated (nothing to wait for anymore).
        Assert.Equal("Salad only", moved.AttendingOption!.Label);
        Assert.Empty(moved.Waitlist);
        Assert.False(moved.Rsvps.Single(r => r.UserId == 582).Waitlisted);

        // Role re-sync: old-attending members revoked, new-attending members granted.
        var revokes = await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        Assert.Contains(revokes, r => r.UserId == 581);
        var grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Contains(grants, g => g.UserId == 583);
    }

    // ---------- Close early ----------

    [Fact]
    public async Task Relative_cutoff_resolves_against_start_and_tracks_time_edits()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Relative cutoff", "in 10 hours", ChannelId, RsvpCloseText: "2h before"));

        Assert.NotNull(ev.RsvpClosesAtUtc);
        Assert.Equal(ev.StartsAtUtc.AddHours(-2), ev.RsvpClosesAtUtc!.Value);

        var moved = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: "in 20 hours")));
        Assert.Equal(moved.StartsAtUtc.AddHours(-2), moved.RsvpClosesAtUtc!.Value);
    }

    [Fact]
    public async Task Absolute_cutoff_parses_as_natural_language_and_clear_reopens()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Absolute cutoff", "in 10 hours", ChannelId, RsvpCloseText: "in 4 hours"));
        Assert.NotNull(ev.RsvpClosesAtUtc);
        Assert.True(ev.RsvpClosesAtUtc < ev.StartsAtUtc);

        var cleared = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearRsvpClose: true)));
        Assert.Null(cleared.RsvpClosesAtUtc);
    }

    [Fact]
    public async Task A_cutoff_already_in_the_past_is_rejected_at_create()
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Too soon", "in 1 hour", ChannelId, RsvpCloseText: "2h before"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_absolute_cutoff_at_or_after_start_is_rejected_at_create()
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Cutoff after start", "in 1 hour", ChannelId, RsvpCloseText: "in 2 hours"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Moving_the_start_before_an_absolute_cutoff_is_rejected_at_edit()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Start vs cutoff", "in 10 hours", ChannelId, RsvpCloseText: "in 8 hours"));

        var moved = await Client.PatchAsJsonAsync(
            $"/events/{ev.Id}", new UpdateEventRequest(CreatorId, WhenText: "in 4 hours"));

        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
    }

    [Fact]
    public async Task Closed_rsvps_reject_puts_and_deletes_with_409()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Frozen list", "in 10 hours", ChannelId, RsvpCloseText: "1h before"));
        var going = ev.AttendingOption!;
        await RsvpAsync(ev.Id, 591, going.Id);

        // Push the cutoff into the past directly — the API computes closedness per request.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.RsvpCloseMinutesBefore, (int?)null)
                    .SetProperty(e => e.RsvpClosesAt, past));
        }

        var put = await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/592", new RsvpRequest(going.Id));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);

        var delete = await Client.DeleteAsync($"/events/{ev.Id}/rsvps/591");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // Reopening via clear makes both work again.
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, ClearRsvpClose: true)))
            .EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/591")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Sweep_syncs_the_embed_once_when_the_cutoff_passes()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Cutoff sweep", "in 10 hours", ChannelId, RsvpCloseText: "1h before"));
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 777301)))
            .EnsureSuccessStatusCode();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5));
            await db.Events.Where(e => e.Id == ev.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.RsvpCloseMinutesBefore, (int?)null)
                    .SetProperty(e => e.RsvpClosesAt, past));
        }

        await SweepAsync();
        await SweepAsync(); // one-shot — the second sweep must not re-enqueue

        await using var checkScope = fixture.Factory.Services.CreateAsyncScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var syncs = await checkDb.Deliveries
            .Where(d => d.Type == DeliveryType.SyncEventMessage && d.PayloadJson.Contains(ev.Id.ToString()))
            .CountAsync();
        Assert.Equal(1, syncs);
    }

    // ---------- Series + custom options ----------

    [Fact]
    public async Task Skipping_a_recurring_event_carries_custom_options_to_the_next_occurrence()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Rolling options", "in 3 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week),
            RsvpOptions: [new RsvpOptionSpec("⚔️", "Raider", 10), new RsvpOptionSpec("❌", "Out")],
            RsvpCloseText: "1h before"));

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;

        Assert.Equal(["Raider", "Out"], next.Options.Select(o => o.Label));
        Assert.Equal(10, next.AttendingOption!.Capacity);
        Assert.True(next.AttendingOption.IsAttending);
        Assert.DoesNotContain(next.Options, o => ev.Options.Any(old => old.Id == o.Id)); // fresh rows
        Assert.Equal(next.StartsAtUtc.AddHours(-1), next.RsvpClosesAtUtc); // relative cutoff inherited
    }

    [Fact]
    public async Task Occurrence_scoped_option_edits_diverge_and_series_scoped_edits_update_the_template()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Scoped option edits", "in 3 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week)));

        // Occurrence scope: this occurrence diverges; the next spawn reverts to the template.
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Occurrence,
            RsvpOptions: [new RsvpOptionSpec("🎲", "One-off", IsAttending: true)])))
            .EnsureSuccessStatusCode();

        var firstSkip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        firstSkip.EnsureSuccessStatusCode();
        var afterOccurrenceEdit = (await firstSkip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(["Going", "Not going", "Maybe"], afterOccurrenceEdit.Options.Select(o => o.Label));

        // Series scope: the edited set becomes the template future occurrences spawn from.
        (await Client.PatchAsJsonAsync($"/events/{afterOccurrenceEdit.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Series,
            RsvpOptions:
            [
                new RsvpOptionSpec("⚔️", "Raider", 5, IsAttending: true),
                new RsvpOptionSpec("❌", "Out"),
            ])))
            .EnsureSuccessStatusCode();

        var secondSkip = await Client.PostAsync($"/events/{afterOccurrenceEdit.Id}/skip", null);
        secondSkip.EnsureSuccessStatusCode();
        var afterSeriesEdit = (await secondSkip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(["Raider", "Out"], afterSeriesEdit.Options.Select(o => o.Label));
        Assert.Equal(5, afterSeriesEdit.AttendingOption!.Capacity);
    }

    [Fact]
    public async Task Series_scoped_absolute_cutoff_edits_dont_clear_the_relative_template()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Cutoff template", "in 3 hours", ChannelId,
            Recurrence: new RecurrenceRuleDto(RecurrenceUnit.Week), RsvpCloseText: "1h before"));

        // An absolute cutoff is occurrence-only — the series' relative template must survive it.
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Series, RsvpCloseText: "in 1 hour"))).EnsureSuccessStatusCode();

        var skip = await Client.PostAsync($"/events/{ev.Id}/skip", null);
        skip.EnsureSuccessStatusCode();
        var next = (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
        Assert.Equal(next.StartsAtUtc.AddHours(-1), next.RsvpClosesAtUtc);
    }

    [Fact]
    public async Task Concurrent_rsvps_to_the_last_seat_never_double_seat()
    {
        var ev = await CreateAsync(new CreateEventRequest(
            CreatorId, "Race for a seat", "in 3 hours", ChannelId, AttendeeLimit: 1));
        var going = ev.AttendingOption!;

        // The per-event row lock serializes these — exactly one gets the seat, the other queues.
        var responses = await Task.WhenAll(
            Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/601", new RsvpRequest(going.Id)),
            Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/602", new RsvpRequest(going.Id)));
        Assert.All(responses, r => r.EnsureSuccessStatusCode());

        var after = (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!;
        Assert.Equal(1, after.SeatedCount(going.Id));
        Assert.Single(after.Waitlist);
    }

    // ---------- helpers ----------

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

    private async Task<List<WaitlistPromotionPayload>> PromotionsAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.WaitlistPromotion && d.PayloadJson.Contains(eventId.ToString()))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
        return [.. rows.Select(d =>
            System.Text.Json.JsonSerializer.Deserialize<WaitlistPromotionPayload>(d.PayloadJson)!)];
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

    /// <summary>Marks pending deliveries of one type as served so later opposite-type enqueues
    /// can't coalesce them away (mirrors AttendeeRoleApiTests).</summary>
    private async Task MarkServedAsync(Guid eventId, DeliveryType type)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        await db.Deliveries
            .Where(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString()))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, 1));
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
    }
}
