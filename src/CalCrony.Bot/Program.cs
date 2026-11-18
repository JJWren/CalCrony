using CalCrony.Bot;
using CalCrony.Bot.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

// GuildMembers is a privileged intent: Discord refuses a connection that asks for it when the
// application does not have it. Discord:ServerMembersIntent decides whether to ask (auto by
// default: only when the application has it), before the client exists — its intents are fixed
// when it is built. An unknown value stops the bot here.
var membersIntent = await ServerMembersIntent.ResolveAsync(builder.Configuration);
builder.Services.AddSingleton(membersIntent);
builder.Services.AddSingleton(new DiscordSocketConfig
{
    GatewayIntents = membersIntent.GatewayIntents,
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

builder.Services.AddSingleton<NativeEventMirror>();
builder.Services.AddSingleton<AttendeeRoleManager>();
builder.Services.AddSingleton<EventThreadManager>();
builder.Services.AddSingleton<LiveListManager>();
builder.Services.AddSingleton<RoleSnapshotService>();

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<DeliveryPollerService>();
builder.Services.AddHostedService<RoleSnapshotReconcileService>();

var host = builder.Build();
host.Run();
