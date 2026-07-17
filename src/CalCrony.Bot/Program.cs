using CalCrony.Bot;
using CalCrony.Bot.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(new DiscordSocketConfig
{
    // GuildMembers is a privileged intent — must also be enabled for the bot application in the
    // Discord Developer Portal (Bot settings → "Server Members Intent"), or gateway login fails.
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
});
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(sp => new InteractionService(
    sp.GetRequiredService<DiscordSocketClient>(),
    new InteractionServiceConfig { DefaultRunMode = RunMode.Async }));

builder.Services.AddHttpClient<CalCronyApiClient>((sp, http) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    http.BaseAddress = new Uri(config["Api:BaseUrl"] ?? "http://localhost:8080");
    http.DefaultRequestHeaders.Add("X-Api-Key", config["Api:ApiKey"] ?? "");
});

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<DeliveryPollerService>();

var host = builder.Build();
host.Run();
