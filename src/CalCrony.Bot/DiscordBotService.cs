using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace CalCrony.Bot;

public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<DiscordBotService> logger) : IHostedService
{
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopAsync();
        await client.LogoutAsync();
    }

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

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(client, interaction);
        var result = await interactions.ExecuteCommandAsync(context, services);
        if (!result.IsSuccess)
        {
            logger.LogWarning("Interaction failed: {Error} — {Reason}", result.Error, result.ErrorReason);
        }
    }

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
