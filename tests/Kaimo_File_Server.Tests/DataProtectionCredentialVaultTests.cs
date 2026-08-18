using System.Text.Json;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DataProtectionCredentialVaultTests
{
    private static readonly CredentialContext Context =
        CredentialContext.ForAuthorizationGrant(Guid.Parse("c8be1af5-266f-43d5-88f8-393461ef275d"), "onedrive");

    [Fact]
    public void Protect_RoundTripsWithoutPersistingPlaintext()
    {
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var credentials = new Dictionary<string, string>
        {
            ["refreshToken"] = "highly-sensitive-token",
            ["scope"] = "Files.ReadWrite offline_access"
        };

        var protectedValue = vault.Protect(credentials, Context);
        var restored = vault.Unprotect<Dictionary<string, string>>(protectedValue, Context);

        Assert.StartsWith("dp:v2:", protectedValue);
        Assert.DoesNotContain("highly-sensitive-token", protectedValue, StringComparison.Ordinal);
        Assert.Equal(credentials, restored);
    }

    [Fact]
    public void Unprotect_RejectsPayloadCopiedToAnotherConnection()
    {
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var protectedValue = vault.Protect(new { RefreshToken = "secret" }, Context);
        var otherConnection = Context with { ConnectionId = Guid.NewGuid() };

        Assert.ThrowsAny<Exception>(() =>
            vault.Unprotect<Dictionary<string, string>>(protectedValue, otherConnection));
    }

    [Fact]
    public void Unprotect_RejectsPayloadCopiedToAnotherProvider()
    {
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var protectedValue = vault.Protect(new { RefreshToken = "secret" }, Context);
        var otherProvider = Context with { ProviderId = "google" };

        Assert.ThrowsAny<Exception>(() =>
            vault.Unprotect<Dictionary<string, string>>(protectedValue, otherProvider));
    }

    [Fact]
    public void Unprotect_ReadsLegacyCloudAccessPayloadDuringMigration()
    {
        var provider = new EphemeralDataProtectionProvider();
        var legacyProtector = provider.CreateProtector("KaimoFiles.CloudAccess.Credentials", "v1");
        var credentials = new Dictionary<string, string> { ["refreshToken"] = "legacy-token" };
        var legacyValue = "dp:v1:" + legacyProtector.Protect(JsonSerializer.Serialize(credentials));
        var vault = new DataProtectionCredentialVault(provider);

        var restored = vault.Unprotect<Dictionary<string, string>>(legacyValue, Context);

        Assert.Equal(credentials, restored);
    }

    [Theory]
    [InlineData("plaintext-token")]
    [InlineData("dp:v2:not-valid")]
    [InlineData("dp:v1:not-valid")]
    public void Unprotect_RejectsUnknownOrTamperedPayloads(string protectedValue)
    {
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        Assert.ThrowsAny<Exception>(() =>
            vault.Unprotect<Dictionary<string, string>>(protectedValue, Context));
    }
}
