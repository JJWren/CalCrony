using CalCrony.Bot;
using CalCrony.Bot.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds,
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

var host = builder.Build();
host.Run();
