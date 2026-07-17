using CalCrony.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CalCrony.Api.Tests;

/// <summary>ApiFixture with the real Google provider swapped for <see cref="FakeCalendarProvider"/> —
/// no real network calls, no real OAuth credentials needed in CI.</summary>
public sealed class CalendarApiFixture : ApiFixture
{
    public FakeCalendarProvider Provider { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Only presence is checked (CalendarEndpoints.CreateLinkToken); the values are never used
        // for a real Google call since ICalendarProvider is swapped for the fake below.
        builder.UseSetting("Calendar:Google:ClientId", "test-client-id");
        builder.UseSetting("Api:PublicBaseUrl", "http://localhost:8080");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<ICalendarProvider>();
        Provider = new FakeCalendarProvider();
        services.AddSingleton<ICalendarProvider>(Provider);
    }
}
