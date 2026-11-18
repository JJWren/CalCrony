using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Api.Services;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The bot-written role snapshots behind role-restricted signup (ADR 0004): the watched
/// list the bot reconciles at Ready — restriction roles and, since #167, the attendee roles an
/// event grants — the guild sync that replaces a snapshot wholesale, the member push, and the two
/// paths that drop a snapshot again — retention (no watched role left) and the bot leaving.</summary>
public class RoleSnapshotEndpointTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const long GuildId = 12100;
    private const long ChannelId = 12101;
    private const long CreatorId = 12102;
    private const long LiveEventRole = 995101;
    private const long EndedEventRole = 995102;
    private const long SeriesRole = 995103;
    private const long OpenPollRole = 995104;
    private const long ClosedPollRole = 995105;
    private const long GrantedOnlyRole = 995106;
    private const long SeriesGrantedRole = 995107;

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task The_watched_list_names_restricted_and_granted_roles_from_live_events_running_series_and_open_polls_only()
    {
        // A scheduled event's restriction is watched, and so is a role an event merely grants —
        // the web has no other source for its name (#167)…
        await CreateEventAsync(GuildId, "Live", [LiveEventRole]);
        await CreateEventAsync(GuildId, "Grants only", [], attendeeRoleId: GrantedOnlyRole);

        // …an ended one's is not…
        var ended = await CreateEventAsync(GuildId, "Ended", [EndedEventRole]);
        (await Client.PatchAsJsonAsync($"/events/{ended.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended))).EnsureSuccessStatusCode();

        // …a running series' template is, even with no live occurrence (the next spawn will
        // carry it)…
        var series = await CreateEventAsync(GuildId, "Weekly", [SeriesRole], weekly: true);
        (await Client.PatchAsJsonAsync($"/events/{series.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();
        var grantingSeries = await CreateEventAsync(GuildId, "Weekly grants", [], weekly: true, attendeeRoleId: SeriesGrantedRole);
        (await Client.PatchAsJsonAsync($"/events/{grantingSeries.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();

        // …an open poll's is, a closed poll's is not.
        await CreatePollAsync(GuildId, "Open?", [OpenPollRole]);
        var closed = await CreatePollAsync(GuildId, "Closed?", [ClosedPollRole]);
        (await Client.PostAsync($"/polls/{closed.Id}/close", null)).EnsureSuccessStatusCode();

        // A guild the bot has left is skipped entirely — it can't resolve roles there.
        const long departedGuild = 12110;
        await CreateEventAsync(departedGuild, "Gone", [995110]);
        (await Client.PutAsJsonAsync($"/guilds/{departedGuild}/presence", new GuildPresenceRequest(false)))
            .EnsureSuccessStatusCode();

        var response = await Client.GetFromJsonAsync<WatchedRolesResponse>("/guilds/roles/watched");

        var guild = Assert.Single(response!.Guilds, g => g.GuildId == GuildId);
        Assert.Contains(LiveEventRole, guild.RoleIds);
        Assert.Contains(GrantedOnlyRole, guild.RoleIds);
        Assert.Contains(SeriesRole, guild.RoleIds);
        Assert.Contains(SeriesGrantedRole, guild.RoleIds);
        Assert.Contains(OpenPollRole, guild.RoleIds);
        Assert.DoesNotContain(EndedEventRole, guild.RoleIds);
        Assert.DoesNotContain(ClosedPollRole, guild.RoleIds);
        Assert.DoesNotContain(response.Guilds, g => g.GuildId == departedGuild);
    }

    [Fact]
    public async Task The_per_guild_lookup_returns_that_guilds_roles_and_empty_for_a_guild_with_none()
    {
        const long restricted = 12170;
        const long quiet = 12171;
        const long role = 995170;
        await CreateEventAsync(restricted, "Restricted", [role]);

        var some = await Client.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{restricted}/roles/watched");
        Assert.Equal(restricted, some!.GuildId);
        Assert.Equal([role], some.RoleIds);

        var none = await Client.GetFromJsonAsync<GuildWatchedRolesDto>($"/guilds/{quiet}/roles/watched");
        Assert.Empty(none!.RoleIds);
    }

    [Fact]
    public async Task Sync_replaces_the_snapshot_tombstones_deleted_roles_and_drops_empty_members()
    {
        const long guild = 12120;
        const long tank = 995120;
        const long deleted = 995121;
        const long unwatched = 995122;
        const long blank = 995123;
        await CreateEventAsync(guild, "Watched", [tank, deleted, blank]);

        var first = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [
                new RoleNameDto(tank, "Tank"),
                new RoleNameDto(deleted, null),
                new RoleNameDto(unwatched, "Stale"),
                new RoleNameDto(blank, "   "), // a whitespace-only name is a real, existing role
            ],
            [
                new MemberRolesDto(1201, [tank, unwatched]),
                new MemberRolesDto(1202, []),          // holds none — no row
                new MemberRolesDto(1203, [deleted]),   // holds only a deleted role — no row either
                new MemberRolesDto(1205, [unwatched]), // holds only a role nothing names — no row
            ]));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var roles = await RolesAsync(guild);
        Assert.Equal("Tank", roles[tank]);
        Assert.True(roles.ContainsKey(deleted));
        Assert.Null(roles[deleted]); // checked and gone: the tombstone that makes it vacuous
        Assert.Equal("   ", roles[blank]); // present, so it keeps gating — never mistaken for a tombstone
        // A role the API's own watch list doesn't name is never stored, however the bot's payload
        // was captured — the API decides what a snapshot may hold.
        Assert.False(roles.ContainsKey(unwatched));
        var firstMembers = await MembersAsync(guild);
        Assert.Equal(new[] { 1201L }, firstMembers.Keys);
        Assert.Equal(new[] { tank }, firstMembers[1201]);
        Assert.NotNull(await SyncedAtAsync(guild));

        // A second sync is a replacement, not a merge: the old member and the tombstone go.
        var second = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank")], [new MemberRolesDto(1204, [tank, 777])]));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        Assert.Equal(new[] { tank }, (await RolesAsync(guild)).Keys);
        var members = await MembersAsync(guild);
        Assert.Equal(new[] { 1204L }, members.Keys);
        Assert.Equal(new[] { tank }, members[1204]); // the unknown 777 was never stored
    }

    [Fact]
    public async Task A_member_push_upserts_within_the_known_roles_and_an_empty_set_removes_the_row()
    {
        const long guild = 12130;
        const long tank = 995130;
        const long deleted = 995131;
        await CreateEventAsync(guild, "Watched", [tank, deleted]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank"), new RoleNameDto(deleted, null)], []))).EnsureSuccessStatusCode();

        var insert = await Client.PutAsJsonAsync($"/guilds/{guild}/members/1301/roles",
            new PutMemberRolesRequest([tank, deleted, 777]));
        Assert.Equal(HttpStatusCode.NoContent, insert.StatusCode);
        Assert.Equal(new[] { tank }, (await MembersAsync(guild))[1301]); // only the existing watched role

        var update = await Client.PutAsJsonAsync($"/guilds/{guild}/members/1301/roles",
            new PutMemberRolesRequest([tank]));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(new[] { tank }, (await MembersAsync(guild))[1301]);

        var clear = await Client.PutAsJsonAsync($"/guilds/{guild}/members/1301/roles", new PutMemberRolesRequest([]));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Empty(await MembersAsync(guild));

        // A push holding only unknown roles stores nothing.
        (await Client.PutAsJsonAsync($"/guilds/{guild}/members/1302/roles", new PutMemberRolesRequest([777])))
            .EnsureSuccessStatusCode();
        Assert.Empty(await MembersAsync(guild));
    }

    [Fact]
    public async Task Web_callers_cannot_touch_role_snapshots()
    {
        var (client, _) = await fixture.LoginAsync(12190, (GuildId, "Members Only", true));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/guilds/roles/watched")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/guilds/{GuildId}/roles/watched")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/guilds/{GuildId}/roles/sync", new RoleSyncRequest([], []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/guilds/{GuildId}/members/12190/roles", new PutMemberRolesRequest([]))).StatusCode);
    }

    [Fact]
    public async Task Sync_keeps_a_granted_roles_name_and_the_event_reports_it()
    {
        const long guild = 12181;
        const long role = 995181;
        var ev = await CreateEventAsync(guild, "Grants", [], attendeeRoleId: role);

        // Before #167 the sync trimmed this role away (it restricts nothing), and the web printed
        // "role #995181" for it.
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(role, "Raider")], []))).EnsureSuccessStatusCode();

        Assert.Equal(new[] { role }, (await RolesAsync(guild)).Keys);
        var fetched = await Client.GetFromJsonAsync<EventDto>($"/events/{ev.Id}");
        var granting = Assert.Single(fetched!.Options, o => o.AttendeeRoleId == role);
        Assert.Equal("Raider", granting.AttendeeRoleName);
    }

    [Fact]
    public async Task Granting_a_role_discards_rows_left_from_its_earlier_watch_on_create_edit_and_revival()
    {
        // The attendee-role counterpart of the restriction cases in RoleRestrictedRsvpApiTests: a
        // name left from an earlier watch must not answer for a role newly granted, whichever path
        // names it — the page shows the id until the bot's fresh sync, never a possibly stale name.
        const long guild = 12183;
        const long role = 995183;

        // Create: an earlier restriction watched the role and was synced, then ended — nothing
        // trimmed the row (no reconcile, no retention run).
        var earlier = await CreateEventAsync(guild, "Earlier", [role]);
        await NameAsync(guild, role, "Old name");
        await EndAsync(earlier);
        Assert.Contains(role, (await RolesAsync(guild)).Keys);

        var granting = await CreateEventAsync(guild, "Grants", [], attendeeRoleId: role);
        Assert.DoesNotContain(role, (await RolesAsync(guild)).Keys);
        var fetched = await Client.GetFromJsonAsync<EventDto>($"/events/{granting.Id}");
        Assert.Null(Assert.Single(fetched!.Options, o => o.AttendeeRoleId == role).AttendeeRoleName);

        // Edit: synced under the granting event, which then ends; a plain event is edited to grant
        // the role.
        await NameAsync(guild, role, "Old name");
        await EndAsync(granting);
        var plain = await CreateEventAsync(guild, "Plain", []);
        (await Client.PatchAsJsonAsync($"/events/{plain.Id}", new UpdateEventRequest(CreatorId, AttendeeRoleId: role)))
            .EnsureSuccessStatusCode();
        Assert.DoesNotContain(role, (await RolesAsync(guild)).Keys);
        await EndAsync(plain);

        // Revival: a series grants the role through its template, synced, its occurrence ended and
        // the series stopped (nothing live names the role, row untouched), then revived.
        var weekly = await CreateEventAsync(guild, "Weekly grants", [], weekly: true, attendeeRoleId: role);
        await NameAsync(guild, role, "Old name");
        (await Client.PatchAsJsonAsync($"/events/{weekly.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended, Scope: EditScope.Occurrence))).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/series/{weekly.SeriesId}/stop", null)).EnsureSuccessStatusCode();
        Assert.Contains(role, (await RolesAsync(guild)).Keys);

        (await Client.PatchAsJsonAsync($"/series/{weekly.SeriesId}", new UpdateSeriesRequest(End: SeriesEndChoice.Never)))
            .EnsureSuccessStatusCode();
        Assert.DoesNotContain(role, (await RolesAsync(guild)).Keys);
    }

    [Fact]
    public async Task Retention_keeps_a_snapshot_alive_for_a_granted_role_alone()
    {
        const long guild = 12182;
        const long role = 995182;
        await CreateEventAsync(guild, "Grants only", [], attendeeRoleId: role);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(role, "Raider")], [new MemberRolesDto(1710, [role])]))).EnsureSuccessStatusCode();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await retention.PurgeAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
        }

        // No restriction anywhere, but the granted role still needs its name — the snapshot stays.
        Assert.Single(await RolesAsync(guild));
        Assert.Single(await MembersAsync(guild));
        Assert.NotNull(await SyncedAtAsync(guild));
    }

    [Fact]
    public async Task Retention_drops_the_snapshot_of_a_guild_with_no_live_restriction_left()
    {
        const long idle = 12140;
        const long busy = 12141;
        const long role = 995140;
        await CreateEventAsync(busy, "Still restricted", [role]);
        foreach (var guild in new[] { idle, busy })
        {
            (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
                [new RoleNameDto(role, "Raider")], [new MemberRolesDto(1401, [role])]))).EnsureSuccessStatusCode();
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await retention.PurgeAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
        }

        // The idle guild holds member data for nothing — gone, marker included…
        Assert.Empty(await RolesAsync(idle));
        Assert.Empty(await MembersAsync(idle));
        Assert.Null(await SyncedAtAsync(idle));
        // …the busy guild's snapshot is still answering a live restriction.
        Assert.Single(await RolesAsync(busy));
        Assert.Single(await MembersAsync(busy));
        Assert.NotNull(await SyncedAtAsync(busy));
    }

    [Fact]
    public async Task Retention_trims_a_kept_snapshot_to_the_roles_still_named()
    {
        const long guild = 12160;
        const long stillNamed = 995160;
        const long endedOnly = 995161;
        await CreateEventAsync(guild, "Still live", [stillNamed]);
        var ended = await CreateEventAsync(guild, "Ends", [endedOnly]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(stillNamed, "Raider"), new RoleNameDto(endedOnly, "Guest")],
            [new MemberRolesDto(1601, [stillNamed, endedOnly]), new MemberRolesDto(1602, [endedOnly])]))).EnsureSuccessStatusCode();

        // The event naming Guest ends; nothing tells the bot to re-sync, so the purge must trim.
        (await Client.PatchAsJsonAsync($"/events/{ended.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended))).EnsureSuccessStatusCode();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await retention.PurgeAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
        }

        Assert.Equal(new[] { stillNamed }, (await RolesAsync(guild)).Keys);
        var members = await MembersAsync(guild);
        Assert.Equal(new[] { 1601L }, members.Keys);           // 1602 held only the ended role
        Assert.Equal(new[] { stillNamed }, members[1601]);     // 1601 lost the ended role's id
        Assert.NotNull(await SyncedAtAsync(guild));            // the snapshot itself stays
    }

    [Fact]
    public async Task The_bot_leaving_drops_the_snapshot()
    {
        const long guild = 12150;
        const long role = 995150;
        await CreateEventAsync(guild, "Restricted", [role]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(role, "Raider")], [new MemberRolesDto(1501, [role])]))).EnsureSuccessStatusCode();

        (await Client.PutAsJsonAsync($"/guilds/{guild}/presence", new GuildPresenceRequest(false)))
            .EnsureSuccessStatusCode();

        Assert.Empty(await RolesAsync(guild));
        Assert.Empty(await MembersAsync(guild));
        Assert.Null(await SyncedAtAsync(guild));

        // A sync that was in flight when the bot left lands after the leave: refused, nothing
        // resurrected; a member push likewise stores nothing for an absent guild.
        var late = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(role, "Raider")], [new MemberRolesDto(1501, [role])]));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/members/1502/roles", new PutMemberRolesRequest([role])))
            .EnsureSuccessStatusCode();
        Assert.Empty(await RolesAsync(guild));
        Assert.Empty(await MembersAsync(guild));
        Assert.Null(await SyncedAtAsync(guild));
    }

    [Fact]
    public async Task Retention_drops_a_bot_absent_guilds_snapshot_even_while_it_still_has_a_restriction()
    {
        const long guild = 12180;
        const long role = 995180;
        await CreateEventAsync(guild, "Still restricted", [role]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(role, "Raider")], [new MemberRolesDto(1801, [role])]))).EnsureSuccessStatusCode();

        // Marked absent behind the presence route's back (which would have dropped the rows
        // itself) — the purge is the backstop for whatever such a path leaves behind.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            await db.Guilds.Where(g => g.Id == guild).ExecuteUpdateAsync(s => s.SetProperty(g => g.BotPresent, false));
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await retention.PurgeAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
        }

        Assert.Empty(await RolesAsync(guild));
        Assert.Empty(await MembersAsync(guild));
        Assert.Null(await SyncedAtAsync(guild));
    }

    [Fact]
    public async Task A_sync_without_members_keeps_names_and_tombstones_drops_every_member_row_and_clears_the_marker()
    {
        const long guild = 12191;
        const long tank = 995191;
        const long deleted = 995192;
        const long unwatched = 995193;
        await CreateEventAsync(guild, "Watched", [tank, deleted]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank"), new RoleNameDto(deleted, null)],
            [new MemberRolesDto(1911, [tank])]))).EnsureSuccessStatusCode();
        Assert.NotNull(await SyncedAtAsync(guild));

        // The bot runs without the Server Members intent: it still resolves the watched roles,
        // and says it cannot tell who holds them.
        var namesOnly = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank (renamed)"), new RoleNameDto(deleted, null), new RoleNameDto(unwatched, "Stale")],
            [],
            MembersKnown: false));
        Assert.Equal(HttpStatusCode.NoContent, namesOnly.StatusCode);

        var roles = await RolesAsync(guild);
        Assert.Equal("Tank (renamed)", roles[tank]);
        Assert.True(roles.ContainsKey(deleted));
        Assert.Null(roles[deleted]);
        Assert.False(roles.ContainsKey(unwatched)); // the API's watch list still decides what is kept
        Assert.Empty(await MembersAsync(guild)); // no member data the bot can no longer keep current
        Assert.Null(await SyncedAtAsync(guild)); // so the web cannot read an absent row as "holds none"

        // The intent back: the next full sync stores members and the marker again.
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank")], [new MemberRolesDto(1912, [tank])]))).EnsureSuccessStatusCode();
        Assert.Equal(new[] { 1912L }, (await MembersAsync(guild)).Keys);
        Assert.NotNull(await SyncedAtAsync(guild));
    }

    [Fact]
    public async Task A_sync_that_says_its_members_are_unknown_but_lists_some_is_refused_and_changes_nothing()
    {
        const long guild = 12192;
        const long tank = 995194;
        await CreateEventAsync(guild, "Watched", [tank]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank")], [new MemberRolesDto(1921, [tank])]))).EnsureSuccessStatusCode();
        var syncedAt = await SyncedAtAsync(guild);

        var refused = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Other")], [new MemberRolesDto(1922, [tank])], MembersKnown: false));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "Members must be empty when MembersKnown is false.",
            (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Equal("Tank", (await RolesAsync(guild))[tank]);
        Assert.Equal(new[] { 1921L }, (await MembersAsync(guild)).Keys);
        Assert.Equal(syncedAt, await SyncedAtAsync(guild));
    }

    [Fact]
    public async Task A_sync_from_a_bot_that_does_not_send_the_field_is_read_as_members_known()
    {
        const long guild = 12193;
        const long tank = 995195;
        await CreateEventAsync(guild, "Watched", [tank]);

        // The body a bot older than the field sends.
        var response = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new
        {
            roles = new[] { new { roleId = tank, name = "Tank" } },
            members = new[] { new { userId = 1931L, roleIds = new[] { tank } } },
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { 1931L }, (await MembersAsync(guild)).Keys);
        Assert.NotNull(await SyncedAtAsync(guild));
    }

    [Fact]
    public async Task After_a_sync_without_members_the_web_is_asked_to_rsvp_from_discord_and_names_still_show()
    {
        const long guild = 12194;
        const long raider = 995196;
        const long granted = 995197;
        var (member, memberSession) = await fixture.LoginAsync(1219401, (guild, "G", false));
        var (manager, managerSession) = await fixture.LoginAsync(1219402, (guild, "G", true));
        var ev = await CreateEventAsync(guild, "Raiders only", [raider], attendeeRoleId: granted);

        // A full sync had listed the member as a holder; then the bot lost the intent.
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(raider, "Raider"), new RoleNameDto(granted, "Veteran")],
            [new MemberRolesDto(memberSession.UserId, [raider])]))).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(raider, "Raider"), new RoleNameDto(granted, "Veteran")], [], MembersKnown: false)))
            .EnsureSuccessStatusCode();

        var web = await member.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{memberSession.UserId}", new RsvpRequest(ev.AttendingOption!.Id));
        Assert.Equal(HttpStatusCode.Conflict, web.StatusCode);
        var error = (await web.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal("We can't confirm your roles right now — RSVP from Discord.", error.Error);
        Assert.Equal(ErrorCodes.RoleRestricted, error.Code);

        // A manager still passes, and the bot, which checks Discord itself, still seats the member.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{managerSession.UserId}", new RsvpRequest(ev.AttendingOption.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsJsonAsync(
            $"/events/{ev.Id}/rsvps/{memberSession.UserId}", new RsvpRequest(ev.AttendingOption.Id))).StatusCode);

        // The web still names both roles.
        var fetched = (await member.GetFromJsonAsync<EventDto>($"/events/{ev.Id}"))!;
        Assert.Equal("Raider", Assert.Single(fetched.Options[0].AllowedRoles!).Name);
        Assert.Equal("Veteran", Assert.Single(fetched.Options, o => o.AttendeeRoleId == granted).AttendeeRoleName);
    }

    [Fact]
    public async Task Retention_trims_and_drops_names_kept_without_members_as_it_does_a_full_snapshot()
    {
        const long kept = 12195;
        const long idle = 12196;
        const long live = 995198;
        const long ended = 995199;
        await CreateEventAsync(kept, "Live", [live]);
        var ending = await CreateEventAsync(kept, "Ending", [ended]);
        var idleEnding = await CreateEventAsync(idle, "Ending too", [live]);
        (await Client.PutAsJsonAsync($"/guilds/{kept}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(live, "Live"), new RoleNameDto(ended, "Ended")], [], MembersKnown: false)))
            .EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/guilds/{idle}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(live, "Live")], [], MembersKnown: false))).EnsureSuccessStatusCode();
        await EndAsync(ending);
        await EndAsync(idleEnding);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await retention.PurgeAsync(SystemClock.Instance.GetCurrentInstant(), CancellationToken.None);
        }

        Assert.Equal(new[] { live }, (await RolesAsync(kept)).Keys); // trimmed to the role still named
        Assert.Empty(await RolesAsync(idle)); // nothing named any more: gone
    }

    [Fact]
    public async Task The_bot_found_gone_at_a_presence_sync_drops_names_kept_without_members()
    {
        const long guild = 12197;
        const long role = 995200;
        await CreateEventAsync(guild, "Restricted", [role]);
        (await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(role, "Raider")], [], MembersKnown: false))).EnsureSuccessStatusCode();
        Assert.Single(await RolesAsync(guild));

        // The bot's presence sync lists every other server it is in, and not this one.
        List<long> present;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
            present = await db.Guilds.Where(g => g.BotPresent && g.Id != guild).Select(g => g.Id).ToListAsync();
        }

        (await Client.PutAsJsonAsync("/guilds/presence/sync", new SyncGuildPresenceRequest(
            [.. present.Select(id => new GuildSnapshotDto(id))]))).EnsureSuccessStatusCode();

        Assert.Empty(await RolesAsync(guild));
        Assert.Null(await SyncedAtAsync(guild));
    }

    private async Task<EventDto> CreateEventAsync(
        long guildId, string title, long[] allowedRoleIds, bool weekly = false, long? attendeeRoleId = null)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            CreatorId, title, "in 3 hours", ChannelId,
            Recurrence: weekly ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null,
            AttendeeRoleId: attendeeRoleId,
            AllowedRoleIds: allowedRoleIds.Length == 0 ? null : allowedRoleIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task NameAsync(long guildId, long roleId, string name)
    {
        (await Client.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(roleId, name)], []))).EnsureSuccessStatusCode();
    }

    private async Task EndAsync(EventDto ev)
    {
        (await Client.PatchAsJsonAsync($"/events/{ev.Id}", new UpdateEventRequest(CreatorId, Status: EventStatus.Ended)))
            .EnsureSuccessStatusCode();
    }

    private async Task<PollDto> CreatePollAsync(long guildId, string question, long[] allowedRoleIds)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/polls", new CreatePollRequest(
            CreatorId, question, ChannelId, ["a", "b"], AllowedRoleIds: allowedRoleIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PollDto>())!;
    }

    private async Task<Dictionary<long, string?>> RolesAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.GuildRoles.AsNoTracking().Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, r => r.Name);
    }

    private async Task<Dictionary<long, long[]>> MembersAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return await db.GuildMemberRoles.AsNoTracking().Where(m => m.GuildId == guildId)
            .ToDictionaryAsync(m => m.UserId, m => m.RoleIds);
    }

    private async Task<Instant?> SyncedAtAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return (await db.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guildId))?.RolesSyncedAt;
    }
}
