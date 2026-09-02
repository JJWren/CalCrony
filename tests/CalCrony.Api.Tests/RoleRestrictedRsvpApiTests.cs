using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>RSVP v2 §3.5: role-restricted signup on events (issue #125, ADR 0004). A restriction
/// lives on the option; the bot checks Discord live and is trusted, a web caller is answered
/// from the guild's role snapshot and fails closed; the creator and managers bypass; seats
/// survive role loss; configuration is bot-only, so the web strips and carries over.</summary>
public class RoleRestrictedRsvpApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 12200;
    private const long ChannelId = 12201;
    private const long CreatorId = 12202;
    private const long RaiderRole = 994100;
    private const long OfficerRole = 994200;
    private const long DeletedRole = 994900;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task A_web_caller_holding_the_role_is_seated_and_one_without_it_is_refused_by_name()
    {
        var ev = await CreateAsync("Raiders only", allowedRoleIds: [RaiderRole]);
        var (alice, aliceSession) = await fixture.LoginAsync(12211, (GuildId, "G", false));
        var (bob, bobSession) = await fixture.LoginAsync(12212, (GuildId, "G", false));
        await SyncAsync(GuildId, [(RaiderRole, "Raider")], [(aliceSession.UserId, [RaiderRole])]);

        var seated = await alice.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{aliceSession.UserId}",
            new RsvpRequest(ev.AttendingOption!.Id));
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        // Bob has no member row: after a sync that means "holds none", not "unknown".
        var refused = await bob.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{bobSession.UserId}",
            new RsvpRequest(ev.AttendingOption.Id));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = await Error(refused);
        Assert.Equal("This option is limited to @Raider.", error.Error);
        Assert.Equal(ErrorCodes.RoleRestricted, error.Code); // tells the web it is a role refusal

        // The DTO carries the name for the web's chip.
        var role = Assert.Single(ev.Options[0].AllowedRoles!);
        var named = await alice.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Equal(RaiderRole, role.Id);
        Assert.Equal("Raider", Assert.Single(named!.Options[0].AllowedRoles!).Name);
    }

    [Fact]
    public async Task An_unsynced_guild_refuses_the_web_but_trusts_the_bot()
    {
        const long guild = 12220;
        var ev = await CreateAsync("Never synced", allowedRoleIds: [RaiderRole], guildId: guild);
        var (carol, session) = await fixture.LoginAsync(12221, (guild, "G", false));

        var web = await carol.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}",
            new RsvpRequest(ev.AttendingOption!.Id));
        Assert.Equal(HttpStatusCode.Conflict, web.StatusCode);
        var error = await Error(web);
        Assert.Equal("We can't confirm your roles right now — RSVP from Discord.", error.Error);
        Assert.Equal(ErrorCodes.RoleRestricted, error.Code);

        // The bot already checked Discord live before calling.
        var bot = await Client.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}",
            new RsvpRequest(ev.AttendingOption.Id));
        Assert.Equal(HttpStatusCode.OK, bot.StatusCode);
    }

    [Fact]
    public async Task The_creator_and_server_managers_bypass_the_restriction()
    {
        var (dave, daveSession) = await fixture.LoginAsync(12231, (GuildId, "G", false));
        var (erin, erinSession) = await fixture.LoginAsync(12232, (GuildId, "G", true));
        // Dave is the creator (the bot created it on his behalf); Erin manages the server. Neither
        // holds the role — the guild has been synced and lists nobody.
        var ev = await CreateAsync("Bypass", allowedRoleIds: [OfficerRole], creatorId: daveSession.UserId);
        await SyncAsync(GuildId, [(OfficerRole, "Officer")], []);

        Assert.Equal(HttpStatusCode.OK, (await dave.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{daveSession.UserId}", new RsvpRequest(ev.AttendingOption!.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await erin.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{erinSession.UserId}", new RsvpRequest(ev.AttendingOption.Id))).StatusCode);
    }

    [Fact]
    public async Task The_event_level_restriction_writes_every_option_and_mirrors_back_only_when_they_agree()
    {
        var uniform = await CreateAsync("Whole event", allowedRoleIds: [RaiderRole]);
        Assert.All(uniform.Options, o => Assert.Equal([RaiderRole], o.AllowedRoles!.Select(r => r.Id)));
        Assert.Equal([RaiderRole], uniform.AllowedRoles!.Select(r => r.Id));
        Assert.Equal(3, uniform.RestrictedOptions.Count);

        var mixed = await CreateAsync("Per option", options:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true, AllowedRoleIds: [RaiderRole]),
            new RsvpOptionSpec("❌", "Out"),
        ]);
        Assert.Null(mixed.AllowedRoles); // options differ — read them per option
        Assert.Equal("Tank", Assert.Single(mixed.RestrictedOptions).Label);
        Assert.Null(mixed.SharedRestriction);

        var open = await CreateAsync("Unrestricted");
        Assert.Empty(open.AllowedRoles!);
        Assert.Empty(open.RestrictedOptions);
    }

    [Fact]
    public async Task Giving_the_restriction_twice_or_too_wide_is_rejected()
    {
        var twice = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Twice", "in 3 hours", ChannelId,
            AllowedRoleIds: [RaiderRole],
            RsvpOptions: [new RsvpOptionSpec("✅", "Going", IsAttending: true, AllowedRoleIds: [OfficerRole])]));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Contains("not both", (await Error(twice)).Error);

        // An explicit empty shorthand is still the shorthand (on an edit it clears every option),
        // so it conflicts with a restricted spec the same way.
        var emptyTwice = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Empty twice", "in 3 hours", ChannelId,
            AllowedRoleIds: [],
            RsvpOptions: [new RsvpOptionSpec("✅", "Going", IsAttending: true, AllowedRoleIds: [OfficerRole])]));
        Assert.Equal(HttpStatusCode.BadRequest, emptyTwice.StatusCode);
        Assert.Contains("not both", (await Error(emptyTwice)).Error);

        var wide = await Client.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            CreatorId, "Wide", "in 3 hours", ChannelId, AllowedRoleIds: [1, 2, 3, 4, 5, 6]));
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        Assert.Contains($"at most {RsvpPolicy.MaxAllowedRoles}", (await Error(wide)).Error);
    }

    [Fact]
    public async Task A_web_create_cannot_set_a_restriction_and_a_web_edit_cannot_either()
    {
        await SeedDefaultChannelAsync(GuildId);
        var (frank, _) = await fixture.LoginAsync(12241, (GuildId, "G", false));

        // Both the event-level field and the spec-level one are dropped: the web can't see the
        // roles it would be naming, so it can't name them.
        var created = await frank.PostAsJsonAsync($"/guilds/{GuildId}/events", new CreateEventRequest(
            0, "Web made", "in 3 hours", 0,
            AllowedRoleIds: [RaiderRole],
            RsvpOptions: [new RsvpOptionSpec("✅", "Going", IsAttending: true, AllowedRoleIds: [OfficerRole])]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = (await created.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Empty(ev.RestrictedOptions);

        var edited = await frank.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(0, AllowedRoleIds: [RaiderRole]));
        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
        Assert.Contains("set in Discord", (await Error(edited)).Error);
    }

    [Fact]
    public async Task A_web_option_edit_preserves_the_restriction_it_cannot_see_and_clear_removes_it()
    {
        var (grace, graceSession) = await fixture.LoginAsync(12251, (GuildId, "G", true));
        var ev = await CreateAsync("Carry over", options:
        [
            new RsvpOptionSpec("🛡️", "Tank", 2, IsAttending: true, AllowedRoleIds: [RaiderRole]),
            new RsvpOptionSpec("❌", "Out"),
        ]);

        // The web form resubmits the options it can show — no restriction anywhere in the specs.
        var resubmitted = await grace.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            graceSession.UserId, RsvpOptions:
            [
                new RsvpOptionSpec("🛡️", "Tank", 3, IsAttending: true),
                new RsvpOptionSpec("❌", "Out"),
                new RsvpOptionSpec("🤔", "Maybe"),
            ]));
        Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);
        var kept = (await resubmitted.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(3, kept.Options.Single(o => o.Label == "Tank").Capacity);
        Assert.Equal([RaiderRole], kept.Options.Single(o => o.Label == "Tank").AllowedRoles!.Select(r => r.Id));
        Assert.False(kept.Options.Single(o => o.Label == "Maybe").IsRestricted);

        // Clearing is the one restriction edit the web may make.
        var cleared = await grace.PatchAsJsonAsync($"/events/{ev.Id}",
            new UpdateEventRequest(graceSession.UserId, ClearAllowedRoles: true));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<EventDto>())!.RestrictedOptions);
    }

    [Fact]
    public async Task The_edit_shorthand_restricts_every_option_reaches_the_series_template_and_is_logged()
    {
        var ev = await CreateAsync("Weekly raid", weekly: true, options:
        [
            new RsvpOptionSpec("🛡️", "Tank", IsAttending: true),
            new RsvpOptionSpec("💚", "Healer"),
        ]);

        var restricted = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Series, AllowedRoleIds: [OfficerRole]));
        Assert.Equal(HttpStatusCode.OK, restricted.StatusCode);
        var dto = (await restricted.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.All(dto.Options, o => Assert.Equal([OfficerRole], o.AllowedRoles!.Select(r => r.Id)));
        Assert.Equal([OfficerRole], dto.AllowedRoles!.Select(r => r.Id));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var series = await db.EventSeries.AsNoTracking().FirstAsync(s => s.Id == ev.SeriesId);
        // The next occurrence spawns from this template, so the restriction rides it.
        Assert.All(RsvpPolicy.OptionsFromTemplate(series.RsvpOptionsJson), o => Assert.Equal([OfficerRole], o.AllowedRoleIds));
        Assert.Contains(
            await db.ActionLogEntries.AsNoTracking().Where(a => a.TargetId == ev.Id).ToListAsync(),
            a => a.Summary.Contains("signup restriction"));

        // Giving the shorthand and clearing together is the usual contradiction…
        var both = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Occurrence, AllowedRoleIds: [OfficerRole], ClearAllowedRoles: true));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        // …and clearing alone reaches the template too.
        var cleared = await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(
            CreatorId, Scope: EditScope.Series, ClearAllowedRoles: true));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var reloaded = await db.EventSeries.AsNoTracking().FirstAsync(s => s.Id == ev.SeriesId);
        Assert.All(RsvpPolicy.OptionsFromTemplate(reloaded.RsvpOptionsJson), o => Assert.Empty(o.AllowedRoleIds));
    }

    [Fact]
    public async Task A_seat_survives_losing_the_role_but_re_entry_needs_it_again()
    {
        var ev = await CreateAsync("Seat stands", allowedRoleIds: [RaiderRole]);
        var (heidi, session) = await fixture.LoginAsync(12261, (GuildId, "G", false));
        await SyncAsync(GuildId, [(RaiderRole, "Raider")], [(session.UserId, [RaiderRole])]);
        var going = ev.AttendingOption!.Id;
        var maybe = ev.Options.Single(o => o.Label == "Maybe").Id;
        (await heidi.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(going)))
            .EnsureSuccessStatusCode();

        // Heidi loses the role: the bot pushes an empty set.
        (await Client.PutAsJsonAsync($"/guilds/{GuildId}/members/{session.UserId}/roles",
            new PutMemberRolesRequest([]))).EnsureSuccessStatusCode();

        // The seat stands — the restriction gates entry only, there is no sweep…
        var still = await heidi.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        Assert.Contains(still!.Rsvps, r => r.UserId == session.UserId && r.OptionId == going);

        // …but switching to another restricted option is a new entry, and withdrawing is free.
        Assert.Equal(HttpStatusCode.Forbidden, (await heidi.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(maybe))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await heidi.DeleteAsync($"/events/{ev.Id}/rsvps/{session.UserId}")).StatusCode);
    }

    [Fact]
    public async Task A_deleted_role_is_vacuous_but_an_unchecked_one_fails_closed()
    {
        const long guild = 12270;
        var (ivan, session) = await fixture.LoginAsync(12271, (guild, "G", false));
        var deletedOnly = await CreateAsync("Deleted role", allowedRoleIds: [DeletedRole], guildId: guild);
        var unsynced = await CreateAsync("Unchecked role", allowedRoleIds: [OfficerRole], guildId: guild);
        // The bot checked the guild and found the deleted role gone; it was never asked about Officer.
        await SyncAsync(guild, [(DeletedRole, null)], []);

        // Nobody can hold a deleted role, so the restriction is no restriction at all…
        Assert.Equal(HttpStatusCode.OK, (await ivan.PutAsJsonAsync(
            $"/events/{deletedOnly.Id}/rsvps/{session.UserId}", new RsvpRequest(deletedOnly.AttendingOption!.Id))).StatusCode);

        // …whereas a role the snapshot has no row for cannot be read as anything but "not yet
        // looked at", and the web refuses rather than guesses.
        var refused = await ivan.PutAsJsonAsync(
            $"/events/{unsynced.Id}/rsvps/{session.UserId}", new RsvpRequest(unsynced.AttendingOption!.Id));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task A_snapshot_past_its_lease_fails_closed_until_the_bot_syncs_again()
    {
        const long guild = 12280;
        var ev = await CreateAsync("Lease", allowedRoleIds: [RaiderRole], guildId: guild);
        var (judy, session) = await fixture.LoginAsync(12281, (guild, "G", false));
        await SyncAsync(guild, [(RaiderRole, "Raider")], [(session.UserId, [RaiderRole])]);

        // The bot has been gone longer than the lease: its rows may name former holders, so the
        // web stops answering from them even though Judy's row says she holds the role.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Guilds.Where(g => g.Id == guild).ExecuteUpdateAsync(s => s.SetProperty(
                g => g.RolesSyncedAt,
                SystemClock.Instance.GetCurrentInstant().Minus(RoleRestriction.SnapshotMaxAge + Duration.FromMinutes(1))));
        }

        var stale = await judy.PutAsJsonAsync($"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(ev.AttendingOption!.Id));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // A fresh sync renews the lease.
        await SyncAsync(guild, [(RaiderRole, "Raider")], [(session.UserId, [RaiderRole])]);
        Assert.Equal(HttpStatusCode.OK, (await judy.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{session.UserId}", new RsvpRequest(ev.AttendingOption.Id))).StatusCode);
    }

    [Fact]
    public async Task Restricting_to_a_role_again_discards_rows_left_from_its_earlier_watch()
    {
        const long guild = 12290;
        var (kim, session) = await fixture.LoginAsync(12291, (guild, "G", false));
        // Kim held the role during an earlier restriction, which has since ended; nothing has
        // trimmed the rows yet (no reconcile, no retention run).
        var earlier = await CreateAsync("Earlier", allowedRoleIds: [RaiderRole], guildId: guild);
        await SyncAsync(guild, [(RaiderRole, "Raider")], [(session.UserId, [RaiderRole])]);
        (await Client.PatchAsJsonAsync($"/events/{earlier.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended))).EnsureSuccessStatusCode();

        // The same role is restricted again. Even with the marker fresh, those rows must not
        // answer for the new restriction — Kim may well have lost the role since — so the web
        // fails closed until the bot's post-create sync lands.
        var again = await CreateAsync("Again", allowedRoleIds: [RaiderRole], guildId: guild);
        var stale = await kim.PutAsJsonAsync($"/events/{again.Id}/rsvps/{session.UserId}", new RsvpRequest(again.AttendingOption!.Id));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await SyncAsync(guild, [(RaiderRole, "Raider")], [(session.UserId, [RaiderRole])]);
        Assert.Equal(HttpStatusCode.OK, (await kim.PutAsJsonAsync(
            $"/events/{again.Id}/rsvps/{session.UserId}", new RsvpRequest(again.AttendingOption.Id))).StatusCode);
    }

    private async Task<EventDto> CreateAsync(
        string title, long[]? allowedRoleIds = null, IReadOnlyList<RsvpOptionSpec>? options = null,
        long guildId = GuildId, long creatorId = CreatorId, bool weekly = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            creatorId, title, "in 3 hours", ChannelId,
            Recurrence: weekly ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null,
            RsvpOptions: options,
            AllowedRoleIds: allowedRoleIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task SyncAsync(long guildId, (long RoleId, string? Name)[] roles, (long UserId, long[] RoleIds)[] members)
    {
        var response = await Client.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest(
            [.. roles.Select(r => new RoleNameDto(r.RoleId, r.Name))],
            [.. members.Select(m => new MemberRolesDto(m.UserId, m.RoleIds))]));
        response.EnsureSuccessStatusCode();
    }

    private async Task SeedDefaultChannelAsync(long guildId)
    {
        var response = await Client.PutAsJsonAsync($"/guilds/{guildId}/settings", new GuildSettingsDto("UTC", ChannelId));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ErrorResponse> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
}
