using System.Reflection;
using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb));

builder.Services.AddDbContext<CalCronyDbContext>(o =>
    o.UseNpgsql(
        builder.Configuration.GetConnectionString("CalCrony"),
        npgsql => npgsql.UseNodaTime()));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
builder.Services.AddScoped<ApiKeyValidator>();
builder.Services.AddSingleton<NaturalDateTimeParser>();
builder.Services.AddScoped<DeliveryScheduler>();

builder.Services.AddDataProtection()
    .SetApplicationName("CalCrony.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["Calendar:DataProtectionKeyPath"] ?? "./keys"));
builder.Services.AddSingleton<CalendarTokenProtector>();
builder.Services.AddHttpClient<ICalendarProvider, GoogleCalendarProvider>(
    http => http.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<CalendarAvailabilityService>();

builder.Services.AddScoped<WebTokenService>();
builder.Services.AddScoped<GuildAccessService>();
builder.Services.AddHttpClient<IDiscordAuthProvider, DiscordAuthProvider>(
    http => http.Timeout = TimeSpan.FromSeconds(20));

// Two credentials, one policy world: the bot's X-Api-Key (full trust) and browser JWTs.
// The fallback policy keeps every endpoint secured unless it explicitly opts out.
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = WebTokenService.Issuer,
            ValidAudience = WebTokenService.Audience,
            IssuerSigningKey = builder.Configuration["Auth:Jwt:SigningKey"] is { Length: >= WebTokenService.MinSigningKeyLength }
                ? WebTokenService.SigningKey(builder.Configuration)
                : null,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                if (context.Response.HasStarted)
                {
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid credentials." });
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            ApiKeyAuthenticationHandler.SchemeName, JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
    // Both policies authenticate against BOTH schemes so the wrong-credential case is an
    // authenticated-but-forbidden 403 (diagnosable) rather than a 401 challenge.
    options.AddPolicy("BotOnly", p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName, JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim(ApiKeyAuthenticationHandler.ClientClaim, ApiKeyAuthenticationHandler.BotClientValue));
    options.AddPolicy("UserOnly", p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName, JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireClaim("sub"));
});

var webOrigin = builder.Configuration["Web:Origin"];
builder.Services.AddCors(options => options.AddPolicy("Web", policy =>
{
    if (!string.IsNullOrWhiteSpace(webOrigin))
    {
        policy.WithOrigins(webOrigin.TrimEnd('/')).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
}));

// Behind NPM the scheme arrives via X-Forwarded-Proto; without this, refresh cookies
// would be minted Secure=false/SameSite=Lax and the cross-origin SPA login breaks.
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedProto);

builder.Services.AddHostedService<StartupMigrationService>();
if (builder.Configuration.GetValue("Scheduler:Enabled", true))
{
    builder.Services.AddHostedService<SchedulerBackgroundService>();
}

var app = builder.Build();

if (string.IsNullOrWhiteSpace(webOrigin))
{
    app.Logger.LogWarning("Web:Origin is not configured — browser clients will be blocked by CORS (bot/API-key callers unaffected).");
}

app.UseForwardedHeaders();
app.UseCors("Web");
app.UseAuthentication();
app.UseAuthorization();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";
app.MapGet("/health", () => Results.Ok(new { status = "ok", version })).AllowAnonymous();
app.MapEventEndpoints();
app.MapPollEndpoints();
app.MapSettingsEndpoints();
app.MapNotificationEndpoints();
app.MapDeliveryEndpoints();
app.MapFeedEndpoints();
app.MapCalendarEndpoints();
app.MapOAuthEndpoints();
app.MapAuthEndpoints();
app.MapMeEndpoints();

app.Run();

public partial class Program;
