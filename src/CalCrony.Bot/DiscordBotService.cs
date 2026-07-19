using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot;

/// <summary>Hosts the Discord client: login, slash-command registration, and interaction dispatch.</summary>
/// <param name="client">The Discord socket client.</param>
/// <param name="interactions">The interaction service.</param>
/// <param name="services">The request service provider.</param>
/// <param name="configuration">The application configuration.</param>
/// <param name="logger">The host logger.</param>
public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
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

    /// <summary>Registers slash commands (to the test guild when configured, else globally).</summary>
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
