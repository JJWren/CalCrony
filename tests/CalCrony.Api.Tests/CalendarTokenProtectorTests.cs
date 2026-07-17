using CalCrony.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Api.Tests;

public class CalendarTokenProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_roundtrips()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var protector = new CalendarTokenProtector(provider.GetRequiredService<IDataProtectionProvider>());

        var ciphertext = protector.Protect("super-secret-refresh-token");

        Assert.NotEqual("super-secret-refresh-token", ciphertext);
        Assert.Equal("super-secret-refresh-token", protector.Unprotect(ciphertext));
    }
}
