using CalCrony.Bot;
using CalCrony.Bot.Api;
using Discord.Interactions;
using Discord.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

// GuildMembers is a privileged intent: Discord refuses a connection that asks for it when the
// application does not have it. Discord:ServerMembersIntent decides whether to ask (auto by
// default: only when the application has it, read with a time limit), before the client exists —
// its intents are fixed when it is built. An unknown value stops the bot here.
ServerMembersIntent membersIntent;
using (var startupHttp = new HttpClient())
{
    membersIntent = await ServerMembersIntent.ResolveAsync(builder.Configuration, new ApplicationFlagsReader(startupHttp));
}

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
// One client for the life of the bot: the watch reads the flags every ten minutes.
builder.Services.AddSingleton(new ApplicationFlagsReader(new HttpClient()));

builder.Services.AddSingleton<NativeEventMirror>();
builder.Services.AddSingleton<AttendeeRoleManager>();
builder.Services.AddSingleton<EventThreadManager>();
builder.Services.AddSingleton<LiveListManager>();
builder.Services.AddSingleton<RoleSnapshotService>();
builder.Services.AddSingleton<IntentRestart>();

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<DeliveryPollerService>();
builder.Services.AddHostedService<RoleSnapshotReconcileService>();
builder.Services.AddHostedService<ServerMembersIntentWatch>();

var host = builder.Build();
await host.RunAsync();
