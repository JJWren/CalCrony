using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace CalCrony.Api.Tests;

/// <summary>The bot's snapshot lists — channels, a server's roles and members, and the servers it
/// is in — are refused with a sentence when an entry is missing altogether, before anything is
/// changed; the bot's availability check without <c>userIds</c> gets the sentence the route gives
/// an empty list. Complete lists are read as before.</summary>
public class SnapshotListApiTests(WebAuthFixture fixture) : IClassFixture<WebAuthFixture>
{
    private const string ChannelsRefusal = "Channels can't have an entry missing.";

    private const string RolesRefusal = "Roles and Members can't have an entry missing.";

    private const string GuildsRefusal = "Guilds can't have an entry missing.";

    private const string UserIdsRefusal = "userIds must have between 1 and 50 distinct entries.";

    // Each test case works with ids of its own.
    private static long nextId = 9_830_000;

    private HttpClient Bot => fixture.Client;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_channel_list_with_an_entry_missing_is_refused(int shape)
    {
        var (guildId, channelId, newChannelId) = (NewId(), NewId(), NewId());
        (await Bot.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest([new(channelId, guildId, "before")])))
            .EnsureSuccessStatusCode();
        IReadOnlyList<ChannelSnapshotDto> channels = shape switch
        {
            0 => [null!],
            1 => [new(channelId, guildId, "after"), null!],
            _ => [null!, new(newChannelId, guildId, "new")],
        };

        var response = await Bot.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest(channels));

        await AssertRefusedAsync(response, ChannelsRefusal);
        Assert.Equal("before", await ChannelNameAsync(channelId));
        Assert.Null(await ChannelNameAsync(newChannelId));
    }

    public static TheoryData<string, int> RoleCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var list in new[] { "roles", "members" })
            {
                for (var shape = 0; shape < 3; shape++)
                {
                    data.Add(list, shape);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RoleCases))]
    public async Task A_role_or_member_list_with_an_entry_missing_is_refused(string list, int shape)
    {
        var scene = await NewRoleSceneAsync();
        var before = await RoleSnapshotAsync(scene.GuildId);
        var renamed = new RoleNameDto(scene.TankRole, "Renamed");
        var newMember = new MemberRolesDto(NewId(), [scene.TankRole]);
        IReadOnlyList<RoleNameDto> roles = [renamed];
        IReadOnlyList<MemberRolesDto> members = [newMember];
        if (list == "roles")
        {
            roles = shape switch
            {
                0 => [null!],
                1 => [renamed, null!],
                _ => [null!, renamed],
            };
        }
        else
        {
            members = shape switch
            {
                0 => [null!],
                1 => [newMember, null!],
                _ => [null!, newMember],
            };
        }

        var response = await Bot.PutAsJsonAsync($"/guilds/{scene.GuildId}/roles/sync", new RoleSyncRequest(roles, members));

        await AssertRefusedAsync(response, RolesRefusal);
        Assert.Equal(before, await RoleSnapshotAsync(scene.GuildId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_server_list_with_an_entry_missing_is_refused(int shape)
    {
        var present = await NewRoleSceneAsync();
        var absentId = NewId();
        (await Bot.PutAsJsonAsync($"/guilds/{absentId}/presence", new GuildPresenceRequest(false))).EnsureSuccessStatusCode();
        var before = (await GuildAsync(present.GuildId), await GuildAsync(absentId), await RoleSnapshotAsync(present.GuildId));
        IReadOnlyList<GuildSnapshotDto> guilds = shape switch
        {
            0 => [null!],
            1 => [new(present.GuildId, "Renamed"), null!],
            _ => [null!, new(absentId, "Back")],
        };

        var response = await Bot.PutAsJsonAsync("/guilds/presence/sync", new SyncGuildPresenceRequest(guilds));

        await AssertRefusedAsync(response, GuildsRefusal);
        var after = (await GuildAsync(present.GuildId), await GuildAsync(absentId), await RoleSnapshotAsync(present.GuildId));
        Assert.Equal(before, after);
        Assert.True(after.Item1.BotPresent);
        Assert.False(after.Item2.BotPresent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_availability_check_without_user_ids_gets_the_routes_sentence(int shape)
    {
        var json = shape == 0
            ? """{"startsAtUtc":"2030-01-01T10:00:00Z","endsAtUtc":"2030-01-01T11:00:00Z"}"""
            : """{"userIds":null,"startsAtUtc":"2030-01-01T10:00:00Z","endsAtUtc":"2030-01-01T11:00:00Z"}""";

        var response = await Bot.PostAsync(
            "/calendar/availability", new StringContent(json, Encoding.UTF8, "application/json"));

        await AssertRefusedAsync(response, UserIdsRefusal);
    }

    [Fact]
    public async Task Complete_lists_are_read_as_before()
    {
        var (guildId, channelId, newChannelId) = (NewId(), NewId(), NewId());
        (await Bot.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest([new(channelId, guildId, "before")])))
            .EnsureSuccessStatusCode();
        var channels = await Bot.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest(
            [new(channelId, guildId, "after"), new(newChannelId, guildId, "new")]));
        Assert.Equal(HttpStatusCode.NoContent, channels.StatusCode);
        Assert.Equal("after", await ChannelNameAsync(channelId));
        Assert.Equal("new", await ChannelNameAsync(newChannelId));

        var scene = await NewRoleSceneAsync();
        var memberId = NewId();
        var roles = await Bot.PutAsJsonAsync($"/guilds/{scene.GuildId}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(scene.TankRole, "Renamed")], [new MemberRolesDto(memberId, [scene.TankRole])]));
        Assert.Equal(HttpStatusCode.NoContent, roles.StatusCode);
        var (names, holders, _) = await RoleSnapshotAsync(scene.GuildId);
        Assert.Equal($"{scene.TankRole}=Renamed", names);
        Assert.Equal($"{memberId}:{scene.TankRole}", holders);

        var absentId = NewId();
        (await Bot.PutAsJsonAsync($"/guilds/{absentId}/presence", new GuildPresenceRequest(false))).EnsureSuccessStatusCode();
        var presence = await Bot.PutAsJsonAsync("/guilds/presence/sync", new SyncGuildPresenceRequest(
            [new(scene.GuildId, "Renamed"), new(absentId, "Back")]));
        Assert.True(presence.IsSuccessStatusCode, $"{(int)presence.StatusCode}");
        var renamedGuild = await GuildAsync(scene.GuildId);
        Assert.True(renamedGuild.BotPresent);
        Assert.Equal("Renamed", renamedGuild.Name);
        Assert.True((await GuildAsync(absentId)).BotPresent);

        var userId = NewId();
        var availability = await Bot.PostAsJsonAsync("/calendar/availability", new AvailabilityRequest(
            [userId, userId], DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2)));
        availability.EnsureSuccessStatusCode();
        var result = (await availability.Content.ReadFromJsonAsync<AvailabilityResponse>())!;
        Assert.Equal(CalendarAvailabilityStatus.NotConnected, Assert.Single(result.Results).Status);
    }

    [Fact]
    public async Task Lists_missing_altogether_keep_their_sentences()
    {
        var guildId = NewId();

        await AssertRefusedAsync(
            await Bot.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest(null!)), "Channels is required.");
        await AssertRefusedAsync(
            await Bot.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest(null!, [])),
            "Roles and Members are required (empty lists clear the snapshot).");
        await AssertRefusedAsync(
            await Bot.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest([], null!)),
            "Roles and Members are required (empty lists clear the snapshot).");
        await AssertRefusedAsync(
            await Bot.PutAsJsonAsync("/guilds/presence/sync", new SyncGuildPresenceRequest(null!)),
            "Guilds is required (an empty list marks every guild absent).");
        await AssertRefusedAsync(
            await Bot.PostAsJsonAsync("/calendar/availability", new AvailabilityRequest(
                [], DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2))),
            UserIdsRefusal);
    }

    [Fact]
    public async Task A_web_caller_is_answered_403_as_before()
    {
        var guildId = NewId();
        var (web, _) = await fixture.LoginAsync(NewId(), (guildId, "Server", true));

        var responses = new[]
        {
            await web.PutAsJsonAsync("/channels/sync", new SyncChannelsRequest([null!])),
            await web.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest([null!], [null!])),
            await web.PutAsJsonAsync("/guilds/presence/sync", new SyncGuildPresenceRequest([null!])),
            await web.PostAsync("/calendar/availability", new StringContent(
                """{"startsAtUtc":"2030-01-01T10:00:00Z","endsAtUtc":"2030-01-01T11:00:00Z"}""", Encoding.UTF8, "application/json")),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    private static long NewId() => Interlocked.Increment(ref nextId);

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string sentence)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Equal(sentence, JsonSerializer.Deserialize<ErrorResponse>(body, JsonSerializerOptions.Web)!.Error);
    }

    private sealed record RoleScene(long GuildId, long TankRole);

    /// <summary>A server the bot is in, with a role snapshot: two watched roles, one member holding
    /// the first.</summary>
    private async Task<RoleScene> NewRoleSceneAsync()
    {
        var (guildId, channelId, creatorId, tank, healer, holder) = (NewId(), NewId(), NewId(), NewId(), NewId(), NewId());
        (await Bot.PutAsJsonAsync($"/guilds/{guildId}/presence", new GuildPresenceRequest(true, "Server")))
            .EnsureSuccessStatusCode();
        (await Bot.PostAsJsonAsync($"/guilds/{guildId}/events", new CreateEventRequest(
            creatorId, "Watched", "in 3 hours", channelId, AllowedRoleIds: [tank, healer]))).EnsureSuccessStatusCode();
        (await Bot.PutAsJsonAsync($"/guilds/{guildId}/roles/sync", new RoleSyncRequest(
            [new RoleNameDto(tank, "Tank"), new RoleNameDto(healer, "Healer")],
            [new MemberRolesDto(holder, [tank])]))).EnsureSuccessStatusCode();
        return new RoleScene(guildId, tank);
    }

    private async Task<string?> ChannelNameAsync(long channelId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        return (await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId))?.Name;
    }

    /// <summary>A server's role snapshot, written out so two readings compare as values: its role
    /// names, its members' roles and when it was taken.</summary>
    private async Task<(string Roles, string Members, Instant? SyncedAt)> RoleSnapshotAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var roles = await db.GuildRoles.AsNoTracking().Where(r => r.GuildId == guildId)
            .OrderBy(r => r.RoleId).Select(r => $"{r.RoleId}={r.Name}").ToListAsync();
        var members = await db.GuildMemberRoles.AsNoTracking().Where(m => m.GuildId == guildId)
            .OrderBy(m => m.UserId).ToListAsync();
        var syncedAt = (await db.Guilds.AsNoTracking().FirstAsync(g => g.Id == guildId)).RolesSyncedAt;
        return (
            string.Join(";", roles),
            string.Join(";", members.Select(m => $"{m.UserId}:{string.Join(",", m.RoleIds.Order())}")),
            syncedAt);
    }

    private async Task<(bool BotPresent, string? Name)> GuildAsync(long guildId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var guild = await db.Guilds.AsNoTracking().FirstAsync(g => g.Id == guildId);
        return (guild.BotPresent, guild.Name);
    }
}
