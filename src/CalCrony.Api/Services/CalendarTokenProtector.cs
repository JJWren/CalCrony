using Microsoft.AspNetCore.DataProtection;

namespace CalCrony.Api.Services;

/// <summary>The only class allowed to encrypt/decrypt CalendarConnection token columns.</summary>
public sealed class CalendarTokenProtector
{
    private readonly IDataProtector protector;

    public CalendarTokenProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector("CalCrony.CalendarTokens");
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => protector.Unprotect(ciphertext);
}
