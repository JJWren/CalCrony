using CalCrony.Api.Services;

namespace CalCrony.Api.Tests;

/// <summary>Web:Origin resolution: only a bare http(s) origin becomes a link prefix.</summary>
public class WebOriginTests
{
    [Theory]
    [InlineData("https://calcrony.app", "https://calcrony.app")]
    [InlineData("https://calcrony.app/", "https://calcrony.app")]
    [InlineData("  https://calcrony.app/  ", "https://calcrony.app")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("HTTPS://CalCrony.app", "https://calcrony.app")] // normalized like any URI
    public void Bare_origins_resolve_to_the_authority(string configured, string expected) =>
        Assert.Equal(expected, WebOrigin.Resolve(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("calcrony.app")] // relative
    [InlineData("ftp://calcrony.app")] // not a web scheme
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@calcrony.app")] // credentials would leak into shared links
    [InlineData("https://calcrony.app/app")] // a path breaks appended routes
    [InlineData("https://calcrony.app/?utm=1")]
    [InlineData("https://calcrony.app/#top")]
    public void Anything_but_a_bare_origin_is_rejected(string? configured) =>
        Assert.Null(WebOrigin.Resolve(configured));
}
