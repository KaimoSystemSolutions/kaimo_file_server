using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAccessCredentialProtectorTests
{
    [Fact]
    public void Protect_RoundTripsWithoutPersistingPlaintext()
    {
        var protector = new CloudAccessCredentialProtector(new EphemeralDataProtectionProvider());
        var credentials = new Dictionary<string, string>
        {
            ["refreshToken"] = "highly-sensitive-token",
            ["scope"] = "Files.ReadWrite offline_access"
        };

        var protectedValue = protector.Protect(credentials);
        var restored = protector.Unprotect(protectedValue);

        Assert.StartsWith("dp:v1:", protectedValue);
        Assert.DoesNotContain("highly-sensitive-token", protectedValue, StringComparison.Ordinal);
        Assert.Equal(credentials, restored);
    }

    [Fact]
    public void Unprotect_RejectsUnknownOrTamperedPayloads()
    {
        var protector = new CloudAccessCredentialProtector(new EphemeralDataProtectionProvider());
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect("plaintext-token"));
        Assert.ThrowsAny<Exception>(() => protector.Unprotect("dp:v1:not-valid"));
    }
}
