using System.Security.Cryptography;
using System.Text;
using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CalCrony.Api.Auth;

/// <summary>Checks raw API keys against the stored SHA-256 hashes, with a short positive-hit cache.</summary>
/// <param name="db">The database context.</param>
/// <param name="cache">The memory cache.</param>
public sealed class ApiKeyValidator(CalCronyDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    /// <summary>SHA-256 hex digest used for storage and lookup — raw keys are never persisted.</summary>
    /// <param name="rawKey">The raw API key as presented by the caller.</param>
    /// <returns>The lowercase hex digest.</returns>
    public static string Hash(string rawKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    /// <summary>True when the raw key matches an active stored key.</summary>
    /// <param name="rawKey">The raw API key as presented by the caller.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True when the key matches an active stored key.</returns>
    public async Task<bool> IsValidAsync(string rawKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        var hash = Hash(rawKey);
        if (cache.TryGetValue<bool>(CacheKey(hash), out var cached))
        {
            return cached;
        }

        var valid = await db.ApiKeys.AnyAsync(k => k.KeyHash == hash && !k.Revoked, cancellationToken);
        cache.Set(CacheKey(hash), valid, CacheDuration);
        return valid;
    }

    /// <summary>Cache key for a validated hash.</summary>
    /// <param name="hash">The SHA-256 hex digest.</param>
    /// <returns>The cache key for the hash.</returns>
    private static string CacheKey(string hash) => $"apikey:{hash}";
}
