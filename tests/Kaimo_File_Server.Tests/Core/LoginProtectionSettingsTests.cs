using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The login-protection values bundle: defaults for installations that never set a key,
/// normalization of unusable values, the throttle policies derived from them, and loading
/// from the config store under the documented keys.
/// </summary>
public class LoginProtectionSettingsTests
{
    [Fact]
    public void Default_MatchesDocumentedValues()
    {
        var settings = LoginProtectionSettings.Default;

        Assert.Equal(5, settings.MaxAttempts);
        Assert.Equal(15, settings.LockoutMinutes);
        Assert.Equal(10, settings.AccountWideFactor);
        Assert.Equal(15, settings.IpMaxAttempts);
        Assert.Equal(30, settings.IpLockoutMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Normalize_ReplacesNonPositiveValuesWithDefaults(int value)
    {
        var settings = new LoginProtectionSettings
        {
            MaxAttempts = value, LockoutMinutes = value, AccountWideFactor = value,
            IpMaxAttempts = value, IpLockoutMinutes = value,
        }.Normalize();

        Assert.Equal(LoginProtectionSettings.Default, settings);
    }

    [Fact]
    public void Normalize_ClampsValuesAboveTheUpperBounds()
    {
        var settings = new LoginProtectionSettings
        {
            MaxAttempts = int.MaxValue, LockoutMinutes = int.MaxValue, AccountWideFactor = int.MaxValue,
            IpMaxAttempts = int.MaxValue, IpLockoutMinutes = int.MaxValue,
        }.Normalize();

        Assert.Equal(LoginProtectionSettings.MaxAttemptsLimit, settings.MaxAttempts);
        Assert.Equal(LoginProtectionSettings.MaxLockoutMinutes, settings.LockoutMinutes);
        Assert.Equal(LoginProtectionSettings.MaxAccountWideFactor, settings.AccountWideFactor);
        Assert.Equal(LoginProtectionSettings.MaxAttemptsLimit, settings.IpMaxAttempts);
        Assert.Equal(LoginProtectionSettings.MaxLockoutMinutes, settings.IpLockoutMinutes);
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var settings = new LoginProtectionSettings
        {
            MaxAttempts = 3, LockoutMinutes = 7, AccountWideFactor = 4, IpMaxAttempts = 20, IpLockoutMinutes = 60,
        };

        Assert.Equal(settings, settings.Normalize());
    }

    [Fact]
    public void Policies_AreDerivedFromTheValues()
    {
        var settings = new LoginProtectionSettings
        {
            MaxAttempts = 3, LockoutMinutes = 7, AccountWideFactor = 4, IpMaxAttempts = 20, IpLockoutMinutes = 60,
        };

        Assert.Equal(new LoginThrottlePolicy(3, TimeSpan.FromMinutes(7)), settings.UserPolicy);
        Assert.Equal(new LoginThrottlePolicy(12, TimeSpan.FromMinutes(7)), settings.AccountPolicy);
        Assert.Equal(new LoginThrottlePolicy(20, TimeSpan.FromMinutes(60)), settings.IpPolicy);
    }

    [Fact]
    public void Keys_KeepTheExistingUserKeys_AndAddSecurityLoginKeys()
    {
        // Existing installations keep their tuned values under the original keys.
        Assert.Equal("app.user.maxLoginAttempts", LoginProtectionSettings.MaxAttemptsKey);
        Assert.Equal("app.user.lockoutMinutes", LoginProtectionSettings.LockoutMinutesKey);
        Assert.Equal("security.login.accountWideFactor", LoginProtectionSettings.AccountWideFactorKey);
        Assert.Equal("security.login.ipMaxAttempts", LoginProtectionSettings.IpMaxAttemptsKey);
        Assert.Equal("security.login.ipLockoutMinutes", LoginProtectionSettings.IpLockoutMinutesKey);
    }

    [Fact]
    public async Task Load_ReadsEveryKey_AndNormalizes()
    {
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.MaxAttemptsKey, 5)).ReturnsAsync(4);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.LockoutMinutesKey, 15)).ReturnsAsync(9);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.AccountWideFactorKey, 10)).ReturnsAsync(3);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.IpMaxAttemptsKey, 15)).ReturnsAsync(0);
        config.Setup(c => c.GetIntAsync(LoginProtectionSettings.IpLockoutMinutesKey, 30)).ReturnsAsync(45);

        var settings = await config.Object.LoadLoginProtectionAsync();

        Assert.Equal(4, settings.MaxAttempts);
        Assert.Equal(9, settings.LockoutMinutes);
        Assert.Equal(3, settings.AccountWideFactor);
        Assert.Equal(15, settings.IpMaxAttempts); // 0 is unusable → default
        Assert.Equal(45, settings.IpLockoutMinutes);
    }
}
