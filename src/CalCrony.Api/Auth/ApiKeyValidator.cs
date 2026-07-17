using System.Security.Cryptography;
using System.Text;
using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CalCrony.Api.Auth;

public sealed class ApiKeyValidator(CalCronyDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public static string Hash(string rawKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

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

    private static string CacheKey(string hash) => $"apikey:{hash}";
}
