using CalCrony.Api.Auth;

namespace CalCrony.Api;

/// <summary>Boot-time configuration checks that turn slow-burn misconfigurations into immediate,
/// clearly worded startup failures. Everything validated here previously limped along and failed
/// later as a runtime 500 or a silent 401 loop.</summary>
public static class StartupConfigValidation
{
    /// <summary>Validates auth configuration before the host builds.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <exception cref="InvalidOperationException">When a value is present but unusable, or a
    /// dependent value is missing.</exception>
    public static void Validate(IConfiguration configuration)
    {
        var signingKey = configuration["Auth:Jwt:SigningKey"];
        var signingKeyUsable = signingKey is { Length: >= WebTokenService.MinSigningKeyLength };

        if (!string.IsNullOrWhiteSpace(signingKey) && !signingKeyUsable)
        {
            // A short key silently disabled JWT validation and 500'd at /auth/refresh.
            throw new InvalidOperationException(
                $"Auth:Jwt:SigningKey must be at least {WebTokenService.MinSigningKeyLength} characters. "
                + "Generate one with: openssl rand -base64 32");
        }

        if (!string.IsNullOrWhiteSpace(configuration["Auth:Discord:ClientId"]) && !signingKeyUsable)
        {
            // Web login configured without a signing key: logins would start, then break at
            // token refresh with an opaque 500.
            throw new InvalidOperationException(
                "Web login is configured (Auth:Discord:ClientId is set) but Auth:Jwt:SigningKey is missing or too short — "
                + $"set a key of at least {WebTokenService.MinSigningKeyLength} characters (openssl rand -base64 32).");
        }

        // Both empty is a legitimate bot-only deployment: web auth is simply off.
    }
}
