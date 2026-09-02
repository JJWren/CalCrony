using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>RSVP v2 §3.3: multiple RSVPs per member (issue #125). A per-event opt-in flag turns a
/// PUT from "move my one choice" into "add a seat", removal gains an option-scoped route, roles
/// become a per-user set difference, and everything keyed on the attending option (waitlist,
/// promotion, threads, availability, DM reminders, CSV rows) is unchanged and counts seats.</summary>
public class MultiRsvpApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 12400;
    private const long ChannelId = 12401;
    private const long CreatorId = 12402;
    private const long RaiderRole = 995100;
    private const long TankRole = 995200;
    private const long HealerRole = 995300;

    private HttpClient Client => fixture.Client;

    // ---------- single-choice mode is untouched ----------

    [Fact]
    public async Task Single_choice_mode_still_switches_ignores_a_re_click_and_clears_on_delete()
    {
        var ev = await CreateAsync("Single choice");
        Assert.False(ev.AllowMultipleRsvps); // the default: nothing changes for existing servers
        var (going, maybe) = (ev.Options.Single(o => o.Label == "Going"), ev.Options.Single(o => o.Label == "Maybe"));

        var first = await RsvpAsync(ev.Id, 701, going.Id);
        Assert.Equal([going.Id], first.RsvpsFor(701).Select(r => r.OptionId));

        // Picking another option MOVES the one row — no second seat appears.
        var switched = await RsvpAsync(ev.Id, 701, maybe.Id);
        Assert.Equal([maybe.Id], switched.RsvpsFor(701).Select(r => r.OptionId));

        // Re-clicking the held option is the no-op it always was.
        var reclicked = await RsvpAsync(ev.Id, 701, maybe.Id);
        Assert.Single(reclicked.RsvpsFor(701));

        // Both delete routes clear the one row: the option-scoped one (what the bot and web now
        // call in every mode) and the bare one.
        var byOption = await ReadAsync<EventDto>(await Client.DeleteAsync($"/events/{ev.Id}/rsvps/701/options/{maybe.Id}"));
        Assert.Empty(byOption.RsvpsFor(701));
        await RsvpAsync(ev.Id, 701, going.Id);
        var bare = await ReadAsync<EventDto>(await Client.DeleteAsync($"/events/{ev.Id}/rsvps/701"));
        Assert.Empty(bare.RsvpsFor(701));
    }

    // ---------- adding and removing seats ----------

    [Fact]
    public async Task In_multi_mode_a_put_adds_a_seat_and_leaves_the_others_alone()
    {
        var ev = await CreateRaidAsync("Add a seat");
        Assert.True(ev.AllowMultipleRsvps);
        var (tank, healer, dps) = Roles(ev);

        await RsvpAsync(ev.Id, 711, tank.Id);
        var both = await RsvpAsync(ev.Id, 711, healer.Id);
        Assert.Equal([tank.Id, healer.Id], both.RsvpsFor(711).Select(r => r.OptionId)); // RSVP order
        Assert.All(both.RsvpsFor(711), r => Assert.False(r.Waitlisted));
        Assert.Equal(1, both.SeatedCount(tank.Id));
        Assert.Equal(1, both.SeatedCount(healer.Id));

        // A re-click of a held option is still the no-op (no third row, no queue move).
        var reclicked = await RsvpAsync(ev.Id, 711, tank.Id);
        Assert.Equal(2, reclicked.RsvpsFor(711).Count);

        var three = await RsvpAsync(ev.Id, 711, dps.Id);
        Assert.Equal(3, three.RsvpsFor(711).Count);
    }

    [Fact]
    public async Task The_option_scoped_delete_removes_that_seat_only_and_an_unheld_option_is_a_no_op()
    {
        var ev = await CreateRaidAsync("Drop one");
        var (tank, healer, dps) = Roles(ev);
        await RsvpAsync(ev.Id, 721, tank.Id);
        await RsvpAsync(ev.Id, 721, healer.Id);

        var dropped = await ReadAsync<EventDto>(
            await Client.DeleteAsync($"/events/{ev.Id}/rsvps/721/options/{healer.Id}"));
        Assert.Equal([tank.Id], dropped.RsvpsFor(721).Select(r => r.OptionId));

        // An option the member does not hold — or that isn't on the event — changes nothing.
        var unheld = await ReadAsync<EventDto>(await Client.DeleteAsync($"/events/{ev.Id}/rsvps/721/options/{dps.Id}"));
        Assert.Equal([tank.Id], unheld.RsvpsFor(721).Select(r => r.OptionId));
        var unknown = await ReadAsync<EventDto>(
            await Client.DeleteAsync($"/events/{ev.Id}/rsvps/721/options/{Guid.NewGuid()}"));
        Assert.Equal([tank.Id], unknown.RsvpsFor(721).Select(r => r.OptionId));
    }

    [Fact]
    public async Task The_bare_delete_clears_every_seat_the_member_holds()
    {
        var ev = await CreateRaidAsync("Drop all");
        var (tank, healer, dps) = Roles(ev);
        await RsvpAsync(ev.Id, 731, tank.Id);
        await RsvpAsync(ev.Id, 731, healer.Id);
        await RsvpAsync(ev.Id, 731, dps.Id);
        await RsvpAsync(ev.Id, 732, dps.Id); // somebody else's seat survives

        var cleared = await ReadAsync<EventDto>(await Client.DeleteAsync($"/events/{ev.Id}/rsvps/731"));
        Assert.Empty(cleared.RsvpsFor(731));
        Assert.Equal([dps.Id], cleared.RsvpsFor(732).Select(r => r.OptionId));
    }

    // ---------- capacity and the waitlist count seats ----------

    [Fact]
    public async Task Capacity_counts_seats_so_one_member_can_fill_two_capped_options()
    {
        var ev = await CreateAsync("Seats not people", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", 1, IsAttending: true),
            new RsvpOptionSpec("💚", "Healer", 1),
        ]);
        var (tank, healer) = (ev.Options.Single(o => o.Label == "Tank"), ev.Options.Single(o => o.Label == "Healer"));

        await RsvpAsync(ev.Id, 741, tank.Id);
        await RsvpAsync(ev.Id, 741, healer.Id);

        // The full non-attending option still refuses outright (nothing to wait for)…
        var full = await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/742", new RsvpRequest(healer.Id));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal("\"Healer\" is full.", (await Error(full)).Error);

        // …and the full attending option queues, as it always did.
        var queued = await RsvpAsync(ev.Id, 742, tank.Id);
        Assert.True(Assert.Single(queued.RsvpsFor(742)).Waitlisted);
    }

    [Fact]
    public async Task A_member_seated_elsewhere_still_waitlists_on_the_full_attending_option()
    {
        var ev = await CreateAsync("Queue while seated", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", 1, IsAttending: true),
            new RsvpOptionSpec("💚", "Healer"),
        ]);
        var (tank, healer) = (ev.Options.Single(o => o.Label == "Tank"), ev.Options.Single(o => o.Label == "Healer"));
        await RsvpAsync(ev.Id, 751, tank.Id);

        await RsvpAsync(ev.Id, 752, healer.Id);
        var queued = await RsvpAsync(ev.Id, 752, tank.Id);

        Assert.Collection(
            queued.RsvpsFor(752),
            r => Assert.Equal((healer.Id, false), (r.OptionId, r.Waitlisted)),
            r => Assert.Equal((tank.Id, true), (r.OptionId, r.Waitlisted)));
        Assert.Equal([752L], queued.Waitlist.Select(r => r.UserId));
    }

    [Fact]
    public async Task Dropping_the_attending_seat_promotes_the_queue_and_dropping_another_seat_does_not()
    {
        var ev = await CreateAsync("Promote on the right seat", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", 1, IsAttending: true),
            new RsvpOptionSpec("💚", "Healer"),
        ]);
        var (tank, healer) = (ev.Options.Single(o => o.Label == "Tank"), ev.Options.Single(o => o.Label == "Healer"));
        await RsvpAsync(ev.Id, 761, tank.Id);
        await RsvpAsync(ev.Id, 761, healer.Id);
        await RsvpAsync(ev.Id, 762, tank.Id); // queued behind 761

        // Healer is not the attending seat: nothing frees, 762 keeps waiting.
        var stillQueued = await ReadAsync<EventDto>(
            await Client.DeleteAsync($"/events/{ev.Id}/rsvps/761/options/{healer.Id}"));
        Assert.Equal([762L], stillQueued.Waitlist.Select(r => r.UserId));
        Assert.Empty(await PromotionsAsync(ev.Id));

        // Tank IS: 762 is seated and pinged.
        var promoted = await ReadAsync<EventDto>(
            await Client.DeleteAsync($"/events/{ev.Id}/rsvps/761/options/{tank.Id}"));
        Assert.Empty(promoted.Waitlist);
        Assert.False(Assert.Single(promoted.RsvpsFor(762)).Waitlisted);
        Assert.Equal(762, Assert.Single(await PromotionsAsync(ev.Id)).UserId);
    }

    // ---------- roles are a per-member set ----------

    [Fact]
    public async Task Two_seats_sharing_one_role_grant_it_once_and_dropping_either_keeps_it()
    {
        var ev = await CreateAsync("Shared raider role", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AttendeeRoleId: RaiderRole),
            new RsvpOptionSpec("💚", "Healer", AttendeeRoleId: RaiderRole),
            new RsvpOptionSpec("❌", "Out"),
        ]);
        var (tank, healer) = (ev.Options.Single(o => o.Label == "Tank"), ev.Options.Single(o => o.Label == "Healer"));

        await RsvpAsync(ev.Id, 771, tank.Id);
        Assert.Equal([(771L, RaiderRole)], (await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole)).Select(g => (g.UserId, g.RoleId)));
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        // The second seat carries a role already held: no second grant.
        await RsvpAsync(ev.Id, 771, healer.Id);
        Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole));

        // Dropping Tank keeps the role — Healer still earns it — so no revoke…
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/771/options/{tank.Id}")).EnsureSuccessStatusCode();
        Assert.Empty(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));

        // …and dropping the last seat that carries it is the one revoke.
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/771/options/{healer.Id}")).EnsureSuccessStatusCode();
        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal((771L, RaiderRole), (revoke.UserId, revoke.RoleId));
    }

    [Fact]
    public async Task Two_seats_with_different_roles_grant_both_and_dropping_one_revokes_only_its_role()
    {
        var ev = await CreateRaidAsync("Tank and Healer roles");
        var (tank, healer, _) = Roles(ev);

        await RsvpAsync(ev.Id, 781, tank.Id);
        await RsvpAsync(ev.Id, 781, healer.Id);
        var grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Equal([(781L, TankRole), (781L, HealerRole)], grants.Select(g => (g.UserId, g.RoleId)).Order());
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/781/options/{tank.Id}")).EnsureSuccessStatusCode();
        var revoke = Assert.Single(await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole));
        Assert.Equal((781L, TankRole), (revoke.UserId, revoke.RoleId)); // Healer's role stays

        // The bare delete hands the rest back.
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/781")).EnsureSuccessStatusCode();
        Assert.Equal(
            [TankRole, HealerRole],
            (await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole)).Select(r => r.RoleId).Order());
    }

    [Fact]
    public async Task Ending_or_deleting_the_event_revokes_a_shared_role_once_per_member()
    {
        var ev = await CreateAsync("Shared role sweep", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AttendeeRoleId: RaiderRole),
            new RsvpOptionSpec("💚", "Healer", AttendeeRoleId: RaiderRole),
            new RsvpOptionSpec("⚔️", "DPS", AttendeeRoleId: TankRole),
        ]);
        var (tank, healer, dps) = Roles(ev);
        await RsvpAsync(ev.Id, 841, tank.Id);   // Raider via two seats…
        await RsvpAsync(ev.Id, 841, healer.Id);
        await RsvpAsync(ev.Id, 841, dps.Id);    // …plus a different role via a third
        await RsvpAsync(ev.Id, 842, healer.Id); // Raider once
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        // The event-wide sweep on delete hands back one delivery per (member, role), never one
        // per seat — the same set semantics the per-RSVP paths use.
        (await Client.DeleteAsync($"/events/{ev.Id}")).EnsureSuccessStatusCode();

        var revokes = await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole);
        Assert.Equal(
            [(841L, RaiderRole), (841L, TankRole), (842L, RaiderRole)],
            revokes.Select(r => (r.UserId, r.RoleId)).Order());
    }

    [Fact]
    public async Task Promotion_skips_the_grant_for_a_member_whose_other_seat_already_carries_the_role()
    {
        var ev = await CreateAsync("Promote an already-raider", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", 1, IsAttending: true, AttendeeRoleId: RaiderRole),
            new RsvpOptionSpec("💚", "Healer", AttendeeRoleId: RaiderRole),
        ]);
        var (tank, healer) = (ev.Options.Single(o => o.Label == "Tank"), ev.Options.Single(o => o.Label == "Healer"));
        await RsvpAsync(ev.Id, 791, tank.Id);          // takes the one seat
        await RsvpAsync(ev.Id, 792, healer.Id);        // earns Raider through Healer…
        await RsvpAsync(ev.Id, 792, tank.Id);          // …and queues for Tank
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/791/options/{tank.Id}")).EnsureSuccessStatusCode();

        // 792 is seated and pinged, but not granted again — they already hold the role.
        var after = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.All(after!.RsvpsFor(792), r => Assert.False(r.Waitlisted));
        Assert.Equal(792, Assert.Single(await PromotionsAsync(ev.Id)).UserId);
        var grants = await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        Assert.Equal(2, grants.Count); // 791's and 792's originals, both already served
        Assert.All(grants, g => Assert.Equal(DeliveryStatus.Sent, g.Status));
        // 791 held Raider through Tank only, so theirs comes back.
        Assert.Equal((791L, RaiderRole), (await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole)).Select(r => (r.UserId, r.RoleId)).Single());
    }

    [Fact]
    public async Task Edit_path_role_diff_treats_each_multi_holder_as_a_set()
    {
        var ev = await CreateAsync("Edit-path sets", multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AttendeeRoleId: RaiderRole),
            new RsvpOptionSpec("💚", "Healer"),
        ]);
        var (tank, healer) = (ev.Options.Single(o => o.Label == "Tank"), ev.Options.Single(o => o.Label == "Healer"));
        await RsvpAsync(ev.Id, 801, tank.Id);   // Tank only
        await RsvpAsync(ev.Id, 802, healer.Id); // Healer only
        await RsvpAsync(ev.Id, 803, tank.Id);   // both
        await RsvpAsync(ev.Id, 803, healer.Id);
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);

        // The role MOVES from Tank to Healer: 801 loses it, 802 gains it, 803 — who holds both
        // seats — keeps it with no delivery at all.
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, RsvpOptions:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true),
            new RsvpOptionSpec("💚", "Healer", AttendeeRoleId: RaiderRole),
        ]))).EnsureSuccessStatusCode();
        Assert.Equal([(801L, RaiderRole)], (await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole)).Select(r => (r.UserId, r.RoleId)));
        Assert.Equal([(802L, RaiderRole)], (await RoleDeliveriesAsync(ev.Id, DeliveryType.GrantAttendeeRole, pendingOnly: true)).Select(g => (g.UserId, g.RoleId)));
        await MarkServedAsync(ev.Id, DeliveryType.GrantAttendeeRole);
        await MarkServedAsync(ev.Id, DeliveryType.RevokeAttendeeRole);

        // Healer's role is dropped: 802 and 803 both lose it (803's other seat no longer carries it).
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, RsvpOptions:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true),
            new RsvpOptionSpec("💚", "Healer"),
        ]))).EnsureSuccessStatusCode();
        Assert.Equal(
            [802L, 803L],
            (await RoleDeliveriesAsync(ev.Id, DeliveryType.RevokeAttendeeRole, pendingOnly: true)).Select(r => r.UserId).Order());
    }

    // ---------- the switch itself ----------

    [Fact]
    public async Task Turning_multi_off_with_multi_holders_is_refused_naming_the_count_and_allowed_once_they_pick()
    {
        var ev = await CreateRaidAsync("Turn off");
        var (tank, healer, _) = Roles(ev);
        await RsvpAsync(ev.Id, 811, tank.Id);
        await RsvpAsync(ev.Id, 811, healer.Id);
        await RsvpAsync(ev.Id, 812, tank.Id);
        await RsvpAsync(ev.Id, 812, healer.Id);
        await RsvpAsync(ev.Id, 813, tank.Id);

        var refused = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AllowMultipleRsvps: false));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("2 members hold more than one RSVP — keep multiple RSVPs on, or ask them to pick one.", (await Error(refused)).Error);

        // Nothing else in the refused edit lands either — the check runs before any mutation.
        var untouched = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Title: "Renamed", AllowMultipleRsvps: false));
        Assert.Equal(HttpStatusCode.Conflict, untouched.StatusCode);
        Assert.Equal("Turn off", (await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!.Title);

        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/811/options/{healer.Id}")).EnsureSuccessStatusCode();
        var one = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AllowMultipleRsvps: false));
        Assert.Equal("1 member holds more than one RSVP — keep multiple RSVPs on, or ask them to pick one.", (await Error(one)).Error);

        // Once every member holds at most one, the switch flips — and their seats stay.
        (await Client.DeleteAsync($"/events/{ev.Id}/rsvps/812/options/{tank.Id}")).EnsureSuccessStatusCode();
        var off = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AllowMultipleRsvps: false)));
        Assert.False(off.AllowMultipleRsvps);
        Assert.Equal(3, off.Rsvps.Count);

        // Single-choice semantics resume: 811's next pick moves their row instead of adding.
        var moved = await RsvpAsync(ev.Id, 811, healer.Id);
        Assert.Equal([healer.Id], moved.RsvpsFor(811).Select(r => r.OptionId));
    }

    [Fact]
    public async Task Turning_multi_on_never_fails_and_null_leaves_it_alone()
    {
        var ev = await CreateAsync("Turn on");
        var (going, maybe) = (ev.Options.Single(o => o.Label == "Going"), ev.Options.Single(o => o.Label == "Maybe"));
        await RsvpAsync(ev.Id, 821, going.Id);

        var on = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AllowMultipleRsvps: true)));
        Assert.True(on.AllowMultipleRsvps);
        var added = await RsvpAsync(ev.Id, 821, maybe.Id);
        Assert.Equal(2, added.RsvpsFor(821).Count); // the existing seat is kept, not moved

        var untouched = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Title: "Still on")));
        Assert.True(untouched.AllowMultipleRsvps);
        var again = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AllowMultipleRsvps: true)));
        Assert.True(again.AllowMultipleRsvps);
    }

    [Fact]
    public async Task A_web_caller_may_set_the_flag_on_create_and_edit()
    {
        const long guild = 12410;
        // Web-created embeds post to the guild's default channel, so one must be configured.
        (await Client.PutAsJsonAsync($"/guilds/{guild}/settings", new GuildSettingsDto("UTC", ChannelId))).EnsureSuccessStatusCode();
        var (member, session) = await fixture.LoginAsync(12411, (guild, "G", false));
        var created = await member.PostAsJsonAsync($"/guilds/{guild}/events", new CreateEventRequest(
            session.UserId, "Web multi", "in 3 hours", ChannelId, AllowMultipleRsvps: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = (await created.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.True(ev.AllowMultipleRsvps);

        var off = await ReadAsync<EventDto>(await member.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(session.UserId, AllowMultipleRsvps: false)));
        Assert.False(off.AllowMultipleRsvps);
    }

    // ---------- series template ----------

    [Fact]
    public async Task The_series_template_carries_the_flag_to_the_next_occurrence()
    {
        var ev = await CreateAsync("Weekly multi", multi: true, weekly: true);
        Assert.True(ev.AllowMultipleRsvps);

        var next = await SkipAsync(ev.Id);
        Assert.True(next.AllowMultipleRsvps);
    }

    [Fact]
    public async Task Series_scope_writes_the_template_only_when_the_request_carries_the_flag_and_occurrence_scope_never_does()
    {
        var ev = await CreateAsync("Weekly template", multi: true, weekly: true);

        // A Series-scope edit that says nothing about the flag leaves the template as it is…
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Scope: EditScope.Series, Title: "Renamed")))
            .EnsureSuccessStatusCode();
        var afterRename = await SkipAsync(ev.Id);
        Assert.True(afterRename.AllowMultipleRsvps);

        // …an Occurrence-scope toggle diverges this occurrence only…
        var diverged = await ReadAsync<EventDto>(await Client.PatchAsJsonAsync(
            $"/events/{afterRename.Id}", new UpdateEventRequest(CreatorId, Scope: EditScope.Occurrence, AllowMultipleRsvps: false)));
        Assert.False(diverged.AllowMultipleRsvps);
        var afterOccurrence = await SkipAsync(afterRename.Id);
        Assert.True(afterOccurrence.AllowMultipleRsvps); // the next spawn reverts to the template

        // …and a Series-scope toggle rewrites it.
        (await Client.PatchAsJsonAsync($"/events/{afterOccurrence.Id}", new UpdateEventRequest(CreatorId, Scope: EditScope.Series, AllowMultipleRsvps: false)))
            .EnsureSuccessStatusCode();
        var afterSeries = await SkipAsync(afterOccurrence.Id);
        Assert.False(afterSeries.AllowMultipleRsvps);
    }

    // ---------- consumers that were already multi-safe ----------

    [Fact]
    public async Task The_restriction_gate_stays_per_option_gating_entry_to_a_second_option_but_not_a_re_click()
    {
        const long guild = 12420;
        var ev = await CreateAsync("Gated second seat", multi: true, guildId: guild, options:
        [
            new RsvpOptionSpec("✅", "Going", IsAttending: true),
            new RsvpOptionSpec("🍕", "Dinner", AllowedRoleIds: [RaiderRole]),
        ]);
        var (going, dinner) = (ev.Options.Single(o => o.Label == "Going"), ev.Options.Single(o => o.Label == "Dinner"));
        var (member, session) = await fixture.LoginAsync(12421, (guild, "G", false));
        await SyncAsync(guild, [(RaiderRole, "Raider")], []);

        // The open option seats them; the restricted one refuses them — the seat they already
        // hold does not open the second door.
        (await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        var refused = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(dinner.Id));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(ErrorCodes.RoleRestricted, (await Error(refused)).Code);

        // With the role, the second seat lands beside the first.
        await SyncAsync(guild, [(RaiderRole, "Raider")], [(session.UserId, [RaiderRole])]);
        var seated = await ReadAsync<EventDto>(await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(dinner.Id)));
        Assert.Equal([going.Id, dinner.Id], seated.RsvpsFor(session.UserId).Select(r => r.OptionId));

        // Losing the role later never turns a re-click into a refusal: entry-only gating.
        await SyncAsync(guild, [(RaiderRole, "Raider")], []);
        var reclick = await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(dinner.Id));
        Assert.Equal(HttpStatusCode.OK, reclick.StatusCode);
        Assert.Equal(2, (await reclick.Content.ReadFromJsonAsync<EventDto>())!.RsvpsFor(session.UserId).Count);
    }

    [Fact]
    public async Task Dm_reminders_fan_out_once_per_member_however_many_seats_they_hold()
    {
        const long twoSeats = 12431;
        const long oneSeat = 12432;
        foreach (var userId in new[] { twoSeats, oneSeat })
        {
            (await Client.PutAsJsonAsync($"/users/{userId}/settings", new UserSettingsDto(null, true, DmReminders: true)))
                .EnsureSuccessStatusCode();
        }

        (await Client.PutAsJsonAsync($"/guilds/{GuildId}/presence", new GuildPresenceRequest(true, "The Keep")))
            .EnsureSuccessStatusCode();
        var ev = await CreateAsync("DM once", multi: true);
        var (going, maybe) = (ev.Options.Single(o => o.Label == "Going"), ev.Options.Single(o => o.Label == "Maybe"));
        (await Client.PutAsJsonAsync($"/events/{ev.Id}/message", new SetEventMessageRequest(ChannelId, 424244))).EnsureSuccessStatusCode();
        await RsvpAsync(ev.Id, twoSeats, going.Id);
        await RsvpAsync(ev.Id, twoSeats, maybe.Id);
        await RsvpAsync(ev.Id, oneSeat, going.Id);

        (await Client.PostAsJsonAsync($"/events/{ev.Id}/notifications", new CreateEventNotificationRequest(200, "Bring dice", "@here")))
            .EnsureSuccessStatusCode();
        await SweepAsync(SystemClock.Instance.GetCurrentInstant());

        var reminders = await DmDeliveriesAsync(ev.Id);
        Assert.Equal([twoSeats, oneSeat], reminders.Select(r => r.UserId).Order()); // one each, not three
    }

    [Fact]
    public async Task Availability_lists_a_member_with_two_seats_once()
    {
        var ev = await CreateAsync("Availability once", multi: true);
        var (going, maybe) = (ev.Options.Single(o => o.Label == "Going"), ev.Options.Single(o => o.Label == "Maybe"));
        var (member, session) = await fixture.LoginAsync(12441, (GuildId, "G", false));
        (await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going.Id))).EnsureSuccessStatusCode();
        (await member.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(maybe.Id))).EnsureSuccessStatusCode();

        var response = await member.GetFromJsonAsync<AvailabilityResponse>($"/events/{ev.Id}/availability");

        Assert.Equal(session.UserId, Assert.Single(response!.Results).UserId);
    }

    [Fact]
    public async Task The_csv_export_emits_one_row_per_seat()
    {
        const long guild = 12450;
        (await Client.PutAsJsonAsync($"/guilds/{guild}/settings", new GuildSettingsDto("UTC", ChannelId))).EnsureSuccessStatusCode();
        var ev = await CreateAsync("Two rows", multi: true, guildId: guild);
        var (going, maybe) = (ev.Options.Single(o => o.Label == "Going"), ev.Options.Single(o => o.Label == "Maybe"));
        await RsvpAsync(ev.Id, 12451, going.Id);
        await RsvpAsync(ev.Id, 12451, maybe.Id);

        var (manager, _) = await fixture.LoginAsync(12452, (guild, "G", true));
        var response = await manager.GetAsync($"/guilds/{guild}/export/events.csv");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var rows = lines.Where(l => l.StartsWith(ev.Id.ToString())).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Contains(",✅,Going,true,"));
        Assert.Contains(rows, r => r.Contains(",🤔,Maybe,false,"));
    }

    [Fact]
    public async Task The_action_log_names_the_switch_among_the_edited_fields()
    {
        const long guild = 12460;
        var ev = await CreateAsync("Logged switch", guildId: guild);

        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, AllowMultipleRsvps: true)))
            .EnsureSuccessStatusCode();

        var page = await ReadAsync<ActionLogPageDto>(await Client.GetAsync($"/guilds/{guild}/actions?action=EventEdited"));
        var edited = Assert.Single(page.Entries);
        Assert.Equal("Edited “Logged switch” — multiple RSVPs", edited.Summary);
        Assert.Contains("\"multiple RSVPs\"", edited.DetailsJson);
    }

    // ---------- concurrency ----------

    [Fact]
    public async Task Concurrent_puts_by_one_member_land_on_two_options_and_collapse_on_the_same_one()
    {
        var ev = await CreateRaidAsync("Race myself");
        var (tank, healer, dps) = Roles(ev);

        // Two options at once: the row lock serializes them, and the wider unique index lets
        // both rows stand.
        var twoOptions = await Task.WhenAll(
            Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/861", new RsvpRequest(tank.Id)),
            Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/861", new RsvpRequest(healer.Id)));
        Assert.All(twoOptions, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var after = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal([tank.Id, healer.Id], after!.RsvpsFor(861).Select(r => r.OptionId).OrderBy(id => id == healer.Id));

        // The same option at once: the second waits on the lock, sees the row, and is the no-op.
        var sameOption = await Task.WhenAll(
            Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/861", new RsvpRequest(dps.Id)),
            Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/861", new RsvpRequest(dps.Id)));
        Assert.All(sameOption, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var settled = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(3, settled!.RsvpsFor(861).Count);
        Assert.Single(settled.RsvpsFor(861), r => r.OptionId == dps.Id);
    }

    // ---------- helpers ----------

    /// <summary>Tank (attending) / Healer / DPS, each with its own role, multiple RSVPs on.</summary>
    private async Task<EventDto> CreateRaidAsync(string title) =>
        await CreateAsync(title, multi: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AttendeeRoleId: TankRole),
            new RsvpOptionSpec("💚", "Healer", AttendeeRoleId: HealerRole),
            new RsvpOptionSpec("⚔️", "DPS"),
        ]);

    private static (RsvpOptionDto Tank, RsvpOptionDto Healer, RsvpOptionDto Dps) Roles(EventDto ev) => (
        ev.Options.Single(o => o.Label == "Tank"),
        ev.Options.Single(o => o.Label == "Healer"),
        ev.Options.Single(o => o.Label == "DPS"));

    private async Task<EventDto> CreateAsync(
        string title, bool multi = false, IReadOnlyList<RsvpOptionSpec>? options = null,
        long guildId = GuildId, bool weekly = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            CreatorId, title, "in 3 hours", ChannelId,
            Recurrence: weekly ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null,
            RsvpOptions: options,
            AllowMultipleRsvps: multi));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<EventDto> RsvpAsync(Guid eventId, long userId, Guid optionId) =>
        await ReadAsync<EventDto>(
            await Client.PutAsJsonAsync($"/events/{eventId}/rsvps/{userId}", new RsvpRequest(optionId)));

    private async Task<EventDto> SkipAsync(Guid eventId)
    {
        var skip = await Client.PostAsync($"/events/{eventId}/skip", null);
        skip.EnsureSuccessStatusCode();
        return (await skip.Content.ReadFromJsonAsync<SkipOccurrenceResponse>())!.NextEvent!;
    }

    private async Task SyncAsync(long guildId, (long RoleId, string? Name)[] roles, (long UserId, long[] RoleIds)[] members)
    {
        var response = await Client.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest(
            [.. roles.Select(r => new RoleNameDto(r.RoleId, r.Name))],
            [.. members.Select(m => new MemberRolesDto(m.UserId, m.RoleIds))]));
        response.EnsureSuccessStatusCode();
    }

    private async Task SweepAsync(Instant now)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeliveryScheduler>();
        await scheduler.SweepAsync(now, CancellationToken.None);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorResponse> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;

    private async Task<List<WaitlistPromotionPayload>> PromotionsAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.WaitlistPromotion && d.PayloadJson.Contains(eventId.ToString()))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
        return [.. rows.Select(d => JsonSerializer.Deserialize<WaitlistPromotionPayload>(d.PayloadJson)!)];
    }

    private async Task<List<DmEventReminderPayload>> DmDeliveriesAsync(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == DeliveryType.DmEventReminder && d.PayloadJson.Contains(eventId.ToString()))
            .ToListAsync();
        return [.. rows.Select(d => JsonSerializer.Deserialize<DmEventReminderPayload>(d.PayloadJson)!)];
    }

    /// <summary>Role deliveries for one event, with the row status kept so a test can tell a
    /// freshly enqueued row from one an earlier step already settled.</summary>
    private async Task<List<(long UserId, long RoleId, DeliveryStatus Status)>> RoleDeliveriesAsync(
        Guid eventId, DeliveryType type, bool pendingOnly = false)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var rows = await db.Deliveries
            .Where(d => d.Type == type && d.PayloadJson.Contains(eventId.ToString())
                        && (!pendingOnly || d.Status == DeliveryStatus.Pending))
            .ToListAsync();
        return
        [
            .. rows.Select(d =>
            {
                var payload = JsonSerializer.Deserialize<AttendeeRolePayload>(d.PayloadJson)!;
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
