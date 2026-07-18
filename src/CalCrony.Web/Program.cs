using CalCrony.Web;
using CalCrony.Web.Api;
using CalCrony.Web.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// Singletons (not scoped): IHttpClientFactory builds message handlers in its own DI scope,
// so a scoped token store/state provider there would be a different instance than the UI's —
// stateful in-memory auth requires the shared instance. (FairShare.Web pattern.)
builder.Services.AddSingleton<ITokenStore, InMemoryTokenStore>();
builder.Services.AddSingleton<JwtAuthenticationStateProvider>();
builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthenticationStateProvider>());
builder.Services.AddTransient<AuthTokenHandler>();

var apiBaseUrl = builder.Configuration["Api:BaseUrl"] is { Length: > 0 } configured
    ? configured
    : builder.HostEnvironment.BaseAddress;

builder.Services.AddHttpClient("Api", client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<AuthTokenHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("Api"));
builder.Services.AddScoped<AuthApiClient>();
builder.Services.AddScoped<CalCronyWebApiClient>();

var host = builder.Build();

// The access token lives only in memory; re-hydrate from the HttpOnly refresh cookie
// before first render so an active session survives reloads.
await host.Services.GetRequiredService<AuthApiClient>().TryRefreshAsync();

await host.RunAsync();
