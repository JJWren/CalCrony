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
/// list the bot reconciles at Ready, the guild sync that replaces a snapshot wholesale, the
/// member push, and the two paths that drop a snapshot again — retention (no live restriction
/// left) and the bot leaving.</summary>
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

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task The_watched_list_names_roles_from_live_events_running_series_and_open_polls_only()
    {
        // A scheduled event's restriction is watched…
        await CreateEventAsync(GuildId, "Live", [LiveEventRole]);

        // …an ended one's is not…
        var ended = await CreateEventAsync(GuildId, "Ended", [EndedEventRole]);
        (await Client.PatchAsJsonAsync($"/events/{ended.Id}", new UpdateEventRequest(
            CreatorId, Status: EventStatus.Ended))).EnsureSuccessStatusCode();

        // …a running series' template is, even with no live occurrence (the next spawn will
        // carry it)…
        var series = await CreateEventAsync(GuildId, "Weekly", [SeriesRole], weekly: true);
        (await Client.PatchAsJsonAsync($"/events/{series.Id}", new UpdateEventRequest(
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
        Assert.Contains(SeriesRole, guild.RoleIds);
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

        var first = await Client.PutAsJsonAsync($"/guilds/{guild}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank"), new RoleNameDto(deleted, null)],
            [
                new MemberRolesDto(1201, [tank]),
                new MemberRolesDto(1202, []),          // holds none — no row
                new MemberRolesDto(1203, [deleted]),   // holds only a deleted role — no row either
            ]));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var roles = await RolesAsync(guild);
        Assert.Equal("Tank", roles[tank]);
        Assert.True(roles.ContainsKey(deleted));
        Assert.Null(roles[deleted]); // checked and gone: the tombstone that makes it vacuous
        Assert.Equal(new[] { 1201L }, (await MembersAsync(guild)).Keys);
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
    }

    private async Task<EventDto> CreateEventAsync(long guildId, string title, long[] allowedRoleIds, bool weekly = false)
    {
        var response = await Client.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            CreatorId, title, "in 3 hours", ChannelId,
            Recurrence: weekly ? new RecurrenceRuleDto(RecurrenceUnit.Week) : null,
            AllowedRoleIds: allowedRoleIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
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
