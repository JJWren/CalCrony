namespace CalCrony.Api.Services;

/// <summary>Resolves the configured web-app origin (<c>Web:Origin</c>) for links the API hands
/// out. A malformed value (stray whitespace, not an http/https URL) resolves to null so anonymous
/// endpoints degrade to no links rather than letting <c>new Uri(...)</c> throw — or a non-web
/// scheme leak.</summary>
public static class WebOrigin
{
    /// <summary>The trimmed origin without a trailing slash, or null when unusable.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The origin, or null.</returns>
    public static string? Resolve(IConfiguration configuration)
    {
        var origin = (configuration["Web:Origin"] ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? origin
            : null;
    }
}
