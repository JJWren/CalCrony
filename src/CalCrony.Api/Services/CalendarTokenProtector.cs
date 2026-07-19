using Microsoft.AspNetCore.DataProtection;

namespace CalCrony.Api.Services;

/// <summary>The only class allowed to encrypt/decrypt CalendarConnection token columns.</summary>
public sealed class CalendarTokenProtector
{
    private readonly IDataProtector protector;

    /// <summary>Scopes a Data Protection protector for calendar tokens (keys persist to the dpkeys volume).</summary>
    public CalendarTokenProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector("CalCrony.CalendarTokens");
    }

    /// <summary>Encrypts a provider token for storage.</summary>
    /// <param name="plaintext">The value to encrypt.</param>
    /// <returns>The encrypted value.</returns>
    public string Protect(string plaintext) => protector.Protect(plaintext);

    /// <summary>Decrypts a stored provider token.</summary>
    /// <param name="ciphertext">The encrypted value.</param>
    /// <returns>The decrypted value.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">When the ciphertext was not produced by this key ring (e.g. the dpkeys volume was lost).</exception>
    public string Unprotect(string ciphertext) => protector.Unprotect(ciphertext);
}
