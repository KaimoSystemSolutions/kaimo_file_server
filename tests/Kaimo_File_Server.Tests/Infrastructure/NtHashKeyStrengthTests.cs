using Kaimo_File_Server.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <c>NtHash:EncryptionKey</c> used to be checked only for emptiness: the public
/// development key or a trivially short key were accepted in production.
/// </summary>
public class NtHashKeyStrengthTests
{
    private const string DevKey = "DevOnly-LocalDevelopment-DoNotUseInProduction-NtHashKey";
    private static readonly string StrongKey = new('k', AesGcmNtHashProtector.MinKeyLength);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DevelopmentKey_OutsideDevelopment_Throws(bool isFreshInstall)
    {
        Assert.Throws<InvalidOperationException>(() => AesGcmNtHashProtector.ValidateKeyStrength(
            DevKey, allowDevelopmentKey: false, isFreshInstall, NullLogger.Instance));
    }

    [Fact]
    public void DevelopmentKey_InDevelopment_IsAccepted()
    {
        AesGcmNtHashProtector.ValidateKeyStrength(DevKey, allowDevelopmentKey: true, isFreshInstall: true, NullLogger.Instance);
    }

    [Fact]
    public void ShortKey_OnFreshInstall_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AesGcmNtHashProtector.ValidateKeyStrength(
            "short-key", allowDevelopmentKey: false, isFreshInstall: true, NullLogger.Instance));
    }

    [Fact]
    public void ShortKey_OnExistingInstall_OnlyWarns()
    {
        // Replacing the key would make every stored NT hash unreadable — the admin decides.
        AesGcmNtHashProtector.ValidateKeyStrength(
            "short-key", allowDevelopmentKey: false, isFreshInstall: false, NullLogger.Instance);
    }

    [Fact]
    public void StrongKey_IsAccepted()
    {
        AesGcmNtHashProtector.ValidateKeyStrength(StrongKey, allowDevelopmentKey: false, isFreshInstall: true, NullLogger.Instance);
    }
}
