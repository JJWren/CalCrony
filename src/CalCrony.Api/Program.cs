using CalCrony.Api.Auth;
using CalCrony.Api.Data;
using CalCrony.Api.Endpoints;
using CalCrony.Api.Services;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddScoped<ApiKeyValidator>();
builder.Services.AddSingleton<NaturalDateTimeParser>();
builder.Services.AddHostedService<StartupMigrationService>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapEventEndpoints();
app.MapSettingsEndpoints();

app.Run();

public partial class Program;
