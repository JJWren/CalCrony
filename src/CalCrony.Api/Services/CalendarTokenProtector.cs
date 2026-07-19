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
    public string Protect(string plaintext) => protector.Protect(plaintext);

    /// <summary>Decrypts a stored provider token.</summary>
    public string Unprotect(string ciphertext) => protector.Unprotect(ciphertext);
}
