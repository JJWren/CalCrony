namespace CalCrony.Api.Services;

/// <summary>Resolves the configured web-app origin (<c>Web:Origin</c>) for links the API hands
/// out. Only a bare http(s) origin is accepted — scheme + host (+ port). Anything else (stray
/// whitespace, another scheme, user-info, a path, query, or fragment) resolves to null so anonymous
/// endpoints degrade to no links rather than emitting broken ones or leaking configured
/// credentials into a shared URL.</summary>
public static class WebOrigin
{
    /// <summary>The authority-only origin without a trailing slash, or null when unusable.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The origin, or null.</returns>
    public static string? Resolve(IConfiguration configuration) => Resolve(configuration["Web:Origin"]);

    /// <summary>The authority-only origin for a raw configured value, or null when unusable.</summary>
    /// <param name="configured">The raw <c>Web:Origin</c> value.</param>
    /// <returns>The origin, or null.</returns>
    public static string? Resolve(string? configured)
    {
        var candidate = (configured ?? "").Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
