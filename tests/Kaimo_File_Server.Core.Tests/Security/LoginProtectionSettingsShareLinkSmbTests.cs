using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Security;

/// <summary>
/// The share-link password and SMB lockout values that the "Login protection" settings tab
/// edits alongside the credential-login values: documented keys and defaults, normalization
/// and loading. The services consuming them are wired up separately.
/// </summary>
public class LoginProtectionSettingsShareLinkSmbTests
{
    [Fact]
    public void Default_MatchesDocumentedValues()
    {
        var settings = LoginProtectionSettings.Default;

        Assert.Equal(5, settings.ShareLinkMaxAttemptsPerAddress);
        Assert.Equal(50, settings.ShareLinkMaxAttemptsPerLink);
        Assert.Equal(30, settings.ShareLinkLockoutMinutes);
        Assert.True(settings.SmbLockoutEnabled);
        Assert.Equal(10, settings.SmbLockoutAttempts);
        Assert.Equal(15, settings.SmbLockoutMinutes);
    }

    [Fact]
    public void Keys_MatchThePlannedNames()
    {
        Assert.Equal("security.sharelink.passwordMaxAttemptsPerAddress", LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey);
        Assert.Equal("security.sharelink.passwordMaxAttemptsPerLink", LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey);
        Assert.Equal("security.sharelink.passwordLockoutMinutes", LoginProtectionSettings.ShareLinkLockoutMinutesKey);
        Assert.Equal("services.smb.lockout.enabled", LoginProtectionSettings.SmbLockoutEnabledKey);
        Assert.Equal("services.smb.lockout.attempts", LoginProtectionSettings.SmbLockoutAttemptsKey);
        Assert.Equal("services.smb.lockout.minutes", LoginProtectionSettings.SmbLockoutMinutesKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Normalize_ReplacesNonPositiveValuesWithDefaults(int value)
    {
        var settings = new LoginProtectionSettings
        {
            ShareLinkMaxAttemptsPerAddress = value, ShareLinkMaxAttemptsPerLink = value, ShareLinkLockoutMinutes = value,
            SmbLockoutAttempts = value, SmbLockoutMinutes = value,
        }.Normalize();

        Assert.Equal(LoginProtectionSettings.Default, settings);
    }

    [Fact]
    public void Normalize_ClampsValuesAboveTheUpperBounds_AndKeepsTheSmbSwitch()
    {
        var settings = new LoginProtectionSettings
        {
            ShareLinkMaxAttemptsPerAddress = int.MaxValue, ShareLinkMaxAttemptsPerLink = int.MaxValue,
            ShareLinkLockoutMinutes = int.MaxValue, SmbLockoutEnabled = false,
            SmbLockoutAttempts = int.MaxValue, SmbLockoutMinutes = int.MaxValue,
        }.Normalize();

        Assert.Equal(LoginProtectionSettings.MaxAttemptsLimit, settings.ShareLinkMaxAttemptsPerAddress);
        Assert.Equal(LoginProtectionSettings.MaxAttemptsLimit, settings.ShareLinkMaxAttemptsPerLink);
        Assert.Equal(LoginProtectionSettings.MaxLockoutMinutes, settings.ShareLinkLockoutMinutes);
        Assert.False(settings.SmbLockoutEnabled);
        Assert.Equal(LoginProtectionSettings.MaxAttemptsLimit, settings.SmbLockoutAttempts);
        Assert.Equal(LoginProtectionSettings.MaxLockoutMinutes, settings.SmbLockoutMinutes);
    }

    [Fact]
    public async Task Load_ReadsTheShareLinkAndSmbKeys()
    {
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetIntAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync((string _, int fallback) => fallback);
        config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((string _, bool fallback) => fallback);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey, 5)).ReturnsAsync(3);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey, 50)).ReturnsAsync(40);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.ShareLinkLockoutMinutesKey, 30)).ReturnsAsync(20);
        config.Setup(c => c.GetBoolAsync(LoginProtectionSettings.SmbLockoutEnabledKey, true)).ReturnsAsync(false);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.SmbLockoutAttemptsKey, 10)).ReturnsAsync(8);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.SmbLockoutMinutesKey, 15)).ReturnsAsync(0);

        var settings = await config.Object.LoadLoginProtectionAsync();

        Assert.Equal(3, settings.ShareLinkMaxAttemptsPerAddress);
        Assert.Equal(40, settings.ShareLinkMaxAttemptsPerLink);
        Assert.Equal(20, settings.ShareLinkLockoutMinutes);
        Assert.False(settings.SmbLockoutEnabled);
        Assert.Equal(8, settings.SmbLockoutAttempts);
        Assert.Equal(15, settings.SmbLockoutMinutes); // 0 is unusable → default
    }
}
