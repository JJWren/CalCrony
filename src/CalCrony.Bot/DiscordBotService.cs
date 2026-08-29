using CalCrony.Bot.Api;
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
public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    CalCronyApiClient api,
    IConfiguration configuration,
    ILogger<DiscordBotService> logger) : IHostedService
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

        // Full reconcile: catches joins/leaves that happened while the bot was offline and
        // repopulates presence after a fresh database (e.g. the test stack's nightly reset).
        var result = await api.SyncGuildPresenceAsync([.. client.Guilds.Select(g => (long)g.Id)]);
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
    }

    /// <summary>Marks the guild bot-present so it appears in the web app immediately after an invite.</summary>
    /// <param name="guild">The guild the bot joined.</param>
    private async Task OnJoinedGuildAsync(SocketGuild guild)
    {
        var result = await api.SetGuildPresenceAsync((long)guild.Id, present: true);
        if (!result.Success)
        {
            logger.LogWarning("Failed to record join of guild {GuildId}: {Error}", guild.Id, result.Error);
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
