using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace CalCrony.Api.Auth;

/// <summary>A freshly signed access token and its expiry.</summary>
public sealed record IssuedAccessToken(string Value, Instant ExpiresAt);

/// <summary>A freshly minted refresh token; Raw goes into the HttpOnly cookie, only its hash is stored.</summary>
public sealed record IssuedRefreshToken(string Raw, Instant ExpiresAt);

/// <summary>
/// Mints web-session credentials: short-lived HS256 access JWTs and rotate-on-use refresh
/// tokens (SHA-256-hashed at rest, claimed atomically so concurrent refreshes can't both win).
/// Only the signing key is configuration; lifetimes and issuer/audience are fixed policy.
/// </summary>
public sealed class WebTokenService(CalCronyDbContext db, IConfiguration configuration, IClock clock)
{
    public const int AccessTokenMinutes = 30;
    public const int RefreshTokenDays = 30;
    public const string Issuer = "CalCrony.Api";
    public const string Audience = "CalCrony.Web";
    public const int MinSigningKeyLength = 32;

    public const string NameClaim = "name";
    public const string AvatarClaim = "avatar";

    /// <summary>Issues a short-lived HS256 access token carrying the Discord id and display claims.</summary>
    public IssuedAccessToken IssueAccessToken(long userId, string username, string? avatarHash)
    {
        var expiresAt = clock.GetCurrentInstant() + Duration.FromMinutes(AccessTokenMinutes);

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(NameClaim, username),
        ];
        if (avatarHash is not null)
        {
            claims.Add(new Claim(AvatarClaim, avatarHash));
        }

        var credentials = new SigningCredentials(SigningKey(configuration), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: expiresAt.ToDateTimeUtc(),
            signingCredentials: credentials);

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>Mints and stores a refresh token (hash only), pruning the user's expired rows.</summary>
    public async Task<IssuedRefreshToken> IssueRefreshTokenAsync(long userId, CancellationToken cancellationToken)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = clock.GetCurrentInstant();
        var expiresAt = now + Duration.FromDays(RefreshTokenDays);

        db.WebRefreshTokens.Add(new WebRefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new IssuedRefreshToken(raw, expiresAt);
    }

    /// <summary>Claims the token in a single UPDATE so rotate-on-use holds under concurrency:
    /// of two simultaneous refreshes only one can flip RevokedAt from null.</summary>
    public async Task<WebRefreshToken?> ConsumeRefreshTokenAsync(string rawToken, CancellationToken cancellationToken)
    {
        var hash = Hash(rawToken);
        var now = clock.GetCurrentInstant();

        var claimed = await db.WebRefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

        return claimed == 0
            ? null
            : await db.WebRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }

    /// <summary>The symmetric signing key from configuration — shared by issue and validation paths.</summary>
    public static SymmetricSecurityKey SigningKey(IConfiguration configuration)
    {
        var key = configuration["Auth:Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinSigningKeyLength)
        {
            throw new InvalidOperationException(
                $"Auth:Jwt:SigningKey must be set and at least {MinSigningKeyLength} characters.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>SHA-256 hex digest — refresh tokens are stored hashed, like API keys.</summary>
    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
