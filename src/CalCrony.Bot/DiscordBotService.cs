using CalCrony.Bot.Api;
using CalCrony.Contracts;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>Hosts the Discord client: login, slash-command registration, interaction dispatch,
/// and guild-presence reporting to the API.</summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="interactions">The interaction service.</param>
/// <param name="services">The request service provider.</param>
/// <param name="api">The CalCrony API client.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
/// <param name="liveLists">The live-list manager.</param>
/// <param name="roleSnapshots">The role-snapshot pusher.</param>
public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    CalCronyApiClient api,
    IConfiguration configuration,
    ILogger<DiscordBotService> logger,
    LiveListManager liveLists,
    RoleSnapshotService roleSnapshots) : IHostedService
{
    /// <summary>Wires events, loads interaction modules, and logs the bot in.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        client.Log += OnLogAsync;
        interactions.Log += OnLogAsync;
        client.Ready += OnReadyAsync;
        client.InteractionCreated += OnInteractionAsync;
        client.JoinedGuild += OnJoinedGuildAsync;
        client.LeftGuild += OnLeftGuildAsync;
        client.GuildUpdated += OnGuildUpdatedAsync;
        client.ChannelUpdated += OnChannelUpdatedAsync;
        // Role snapshots (ADR 0004): a member's watched-role delta, a member leaving, a watched
        // role deleted or renamed — each keeps the API's snapshot current between full syncs.
        client.GuildMemberUpdated += roleSnapshots.OnMemberUpdatedAsync;
        client.UserLeft += roleSnapshots.OnUserLeftAsync;
        client.RoleDeleted += roleSnapshots.OnRoleDeletedAsync;
        client.RoleUpdated += roleSnapshots.OnRoleUpdatedAsync;
        client.LeftGuild += roleSnapshots.OnLeftGuildAsync;

        await interactions.AddModulesAsync(typeof(DiscordBotService).Assembly, services);

        var token = configuration["Discord:BotToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Discord:BotToken is not configured; the bot will not connect to Discord.");
            return;
        }

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
    }

    /// <summary>Logs the bot out and disconnects.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopAsync();
        await client.LogoutAsync();
    }

    /// <summary>Registers slash commands (to the test guild when configured, else globally)
    /// and reconciles guild presence with the API.</summary>
    private async Task OnReadyAsync()
    {
        // Guild-scoped registration is instant; global registration can take up to an hour.
        var testGuildId = configuration.GetValue<ulong?>("Discord:TestGuildId");
        if (testGuildId is ulong guildId)
        {
            await interactions.RegisterCommandsToGuildAsync(guildId);
            logger.LogInformation("Registered slash commands to guild {GuildId}.", guildId);
        }
        else
        {
            await interactions.RegisterCommandsGloballyAsync();
            logger.LogInformation("Registered slash commands globally.");
        }

        // Full reconcile: catches joins/leaves (and renames) that happened while the bot was
        // offline and repopulates presence + name snapshots after a fresh database (e.g. the
        // test stack's nightly reset).
        var result = await api.SyncGuildPresenceAsync(
            [.. client.Guilds.Select(g => new GuildSnapshotDto((long)g.Id, g.Name))]);
        if (result is { Success: true, Value: { } counts })
        {
            logger.LogInformation(
                "Synced guild presence: {Present} present, {Absent} absent.", counts.Present, counts.Absent);
        }
        else
        {
            logger.LogWarning(
                "Guild presence sync failed: {Error}", result.Error ?? "empty response body");
        }

        await ReconcileChannelNamesAsync();
        // Role snapshots last: they need the member cache, which the guild sync downloads.
        await roleSnapshots.ReconcileAllAsync();
        await ReconcileLiveListsAsync();
    }

    /// <summary>Self-heals every live list at Ready (the ADR 0001 snapshot-reconcile pattern):
    /// re-renders each recorded list message with the current events, and clears records whose
    /// messages were deleted while the bot was offline — deleted message = list is gone.</summary>
    private async Task ReconcileLiveListsAsync()
    {
        var lists = await api.ListAllLiveListsAsync();
        if (lists is not { Success: true, Value: { } all })
        {
            logger.LogWarning("Live-list lookup failed: {Error}", lists.Error ?? "empty response body");
            return;
        }

        foreach (var list in all)
        {
            try
            {
                await liveLists.SyncAsync(list);
            }
            catch (Exception ex)
            {
                // Per-list isolation; a failed one heals on its next SyncLiveList delivery.
                logger.LogWarning(ex, "Live-list reconcile failed for list {LiveListId}.", list.Id);
            }
        }

        if (all.Count > 0)
        {
            logger.LogInformation("Reconciled {Count} live lists.", all.Count);
        }
    }

    /// <summary>Refreshes channel-name snapshots for every channel the API references — heals a
    /// fresh database and picks up renames missed while offline. Channels that no longer resolve
    /// (deleted, or a guild the bot left) are simply skipped: their stored snapshot stays as the
    /// last-known name.</summary>
    private async Task ReconcileChannelNamesAsync()
    {
        var referenced = await api.GetReferencedChannelsAsync();
        if (referenced is not { Success: true, Value: { } response })
        {
            logger.LogWarning(
                "Referenced-channel lookup failed: {Error}", referenced.Error ?? "empty response body");
            return;
        }

        var snapshots = new List<ChannelSnapshotDto>();
        foreach (var reference in response.Channels)
        {
            var name = client.GetGuild((ulong)reference.GuildId)?.GetChannel((ulong)reference.ChannelId)?.Name;
            if (name is not null)
            {
                snapshots.Add(new ChannelSnapshotDto(reference.ChannelId, reference.GuildId, name));
            }
        }

        if (snapshots.Count == 0)
        {
            return;
        }

        var synced = await api.SyncChannelsAsync(snapshots);
        if (synced.Success)
        {
            logger.LogInformation("Synced {Count} channel-name snapshots.", snapshots.Count);
        }
        else
        {
            logger.LogWarning("Channel-name sync failed: {Error}", synced.Error);
        }
    }

    /// <summary>Marks the guild bot-present so it appears in the web app immediately after an invite.</summary>
    /// <param name="guild">The guild the bot joined.</param>
    private async Task OnJoinedGuildAsync(SocketGuild guild)
    {
        var result = await api.SetGuildPresenceAsync((long)guild.Id, present: true, guild.Name);
        if (!result.Success)
        {
            logger.LogWarning("Failed to record join of guild {GuildId}: {Error}", guild.Id, result.Error);
            return;
        }

        // A re-invited guild may still carry live restrictions whose snapshot the leave dropped —
        // restore it now, so its web members aren't refused until the next reconcile.
        await roleSnapshots.SyncGuildAsync(guild);
    }

    /// <summary>Keeps the guild-name snapshot fresh when a server renames itself.</summary>
    /// <param name="before">The guild before the update.</param>
    /// <param name="after">The guild after the update.</param>
    private async Task OnGuildUpdatedAsync(SocketGuild before, SocketGuild after)
    {
        if (before.Name == after.Name)
        {
            return;
        }

        var result = await api.SetGuildPresenceAsync((long)after.Id, present: true, after.Name);
        if (!result.Success)
        {
            logger.LogWarning("Failed to record rename of guild {GuildId}: {Error}", after.Id, result.Error);
        }
    }

    /// <summary>Keeps channel-name snapshots fresh when a channel is renamed. The API updates
    /// existing snapshots only, so renames of channels CalCrony never references are no-ops.</summary>
    /// <param name="before">The channel before the update.</param>
    /// <param name="after">The channel after the update.</param>
    private async Task OnChannelUpdatedAsync(SocketChannel before, SocketChannel after)
    {
        if (before is not SocketGuildChannel oldChannel
            || after is not SocketGuildChannel newChannel
            || oldChannel.Name == newChannel.Name)
        {
            return;
        }

        var result = await api.SetChannelNameAsync((long)newChannel.Id, newChannel.Name);
        if (!result.Success)
        {
            logger.LogWarning("Failed to record rename of channel {ChannelId}: {Error}", newChannel.Id, result.Error);
        }
    }

    /// <summary>Marks the guild bot-absent; the row (and its settings and data) is kept for a re-invite.</summary>
    /// <param name="guild">The guild the bot left.</param>
    private async Task OnLeftGuildAsync(SocketGuild guild)
    {
        var result = await api.SetGuildPresenceAsync((long)guild.Id, present: false);
        if (!result.Success)
        {
            logger.LogWarning("Failed to record leave of guild {GuildId}: {Error}", guild.Id, result.Error);
        }
    }

    /// <summary>Routes every interaction (commands, components, modals, autocomplete) through the interaction service.</summary>
    /// <param name="interaction">The incoming interaction.</param>
    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(client, interaction);
        var result = await interactions.ExecuteCommandAsync(context, services);
        if (!result.IsSuccess)
        {
            logger.LogWarning("Interaction failed: {Error} — {Reason}", result.Error, result.ErrorReason);
        }
    }

    /// <summary>Bridges Discord.Net logs into the host logger.</summary>
    /// <param name="message">Optional message text.</param>
    private Task OnLogAsync(LogMessage message)
    {
        logger.Log(message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        }, "{Source}: {Message}", message.Source, message.Exception?.ToString() ?? message.Message);
        return Task.CompletedTask;
    }
}
