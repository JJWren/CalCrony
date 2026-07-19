using System.Security.Cryptography;
using System.Text;
using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CalCrony.Api.Auth;

/// <summary>Checks raw API keys against the stored SHA-256 hashes, with a short positive-hit cache.</summary>
public sealed class ApiKeyValidator(CalCronyDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    /// <summary>SHA-256 hex digest used for storage and lookup — raw keys are never persisted.</summary>
    public static string Hash(string rawKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    /// <summary>True when the raw key matches an active stored key.</summary>
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
    private static string CacheKey(string hash) => $"apikey:{hash}";
}
