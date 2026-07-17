using System.Net;
using System.Net.Http.Json;
using CalCrony.Api.Data;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

public class CalendarConnectionTests(CalendarApiFixture fixture) : IClassFixture<CalendarApiFixture>
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Link_token_mint_returns_a_start_url_containing_the_token()
    {
        var dto = await MintTokenDtoAsync(1001);

        Assert.Contains(dto.Token, dto.StartUrl);
        Assert.Contains("/oauth/google/start?token=", dto.StartUrl);
    }

    [Fact]
    public async Task Start_redirects_to_google_with_state_for_a_valid_token()
    {
        var token = (await MintTokenDtoAsync(1002)).Token;

        using var noRedirectClient = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await noRedirectClient.GetAsync($"/oauth/google/start?token={token}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("accounts.google.com", location);
        Assert.Contains($"state={token}", location);
    }

    [Fact]
    public async Task Start_rejects_an_unknown_token_without_redirecting()
    {
        using var noRedirectClient = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await noRedirectClient.GetAsync("/oauth/google/start?token=0000000000000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Full_happy_path_creates_a_connection_and_consumes_the_link_token()
    {
        const long userId = 1003;
        var token = (await MintTokenDtoAsync(userId)).Token;

        var callback = await fixture.Factory.CreateClient()
            .GetAsync($"/oauth/google/callback?code=happy-{userId}&state={token}");

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("Calendar connected", await callback.Content.ReadAsStringAsync());

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        Assert.NotNull(await db.CalendarConnections.SingleOrDefaultAsync(c => c.UserId == userId));
        var linkToken = await db.CalendarLinkTokens.SingleAsync(t => t.Token == token);
        Assert.NotNull(linkToken.ConsumedAt);
    }

    [Fact]
    public async Task Denied_consent_shows_failure_page_and_does_not_consume_the_token()
    {
        var token = (await MintTokenDtoAsync(1004)).Token;

        var callback = await fixture.Factory.CreateClient()
            .GetAsync($"/oauth/google/callback?state={token}&error=access_denied");

        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var linkToken = await db.CalendarLinkTokens.SingleAsync(t => t.Token == token);
        Assert.Null(linkToken.ConsumedAt);
    }

    [Fact]
    public async Task Exchange_failure_shows_failure_page_and_does_not_consume_the_token()
    {
        var token = (await MintTokenDtoAsync(1005)).Token;

        var callback = await fixture.Factory.CreateClient()
            .GetAsync($"/oauth/google/callback?code={FakeCalendarProvider.InvalidCode}&state={token}");

        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CalCronyDbContext>();
        var linkToken = await db.CalendarLinkTokens.SingleAsync(t => t.Token == token);
        Assert.Null(linkToken.ConsumedAt);
    }

    [Fact]
    public async Task Callback_html_encodes_attacker_controlled_error_text()
    {
        var token = (await MintTokenDtoAsync(1007)).Token;

        var callback = await fixture.Factory.CreateClient()
            .GetAsync($"/oauth/google/callback?state={token}&error=<script>alert(1)</script>");

        var body = await callback.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", body);
    }

    [Fact]
    public async Task Disconnect_removes_the_connection_and_revokes_at_the_provider()
    {
        const long userId = 1006;
        var token = (await MintTokenDtoAsync(userId)).Token;
        await fixture.Factory.CreateClient().GetAsync($"/oauth/google/callback?code=disc-{userId}&state={token}");

        var connected = await Client.GetFromJsonAsync<CalendarConnectionStatusDto>($"/calendar/connections/{userId}");
        Assert.True(connected!.Connected);

        var delete = await Client.DeleteAsync($"/calendar/connections/{userId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var disconnected = await Client.GetFromJsonAsync<CalendarConnectionStatusDto>($"/calendar/connections/{userId}");
        Assert.False(disconnected!.Connected);
        Assert.Contains(fixture.Provider.RevokedTokens, t => t == $"refresh-disc-{userId}");
    }

    private async Task<CalendarLinkTokenDto> MintTokenDtoAsync(long userId)
    {
        var response = await Client.PostAsync($"/calendar/connections/{userId}/link-token", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CalendarLinkTokenDto>())!;
    }
}
