using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Infrastructure.Configuration;

/// <summary>Reads <see cref="LoginProtectionSettings"/> from the config store.</summary>
public static class LoginProtectionSettingsLoader
{
    /// <summary>Loads every login-protection value (missing keys use the defaults) and normalizes the result.</summary>
    public static async Task<LoginProtectionSettings> LoadLoginProtectionAsync(this IConfigRepository config)
    {
        var defaults = LoginProtectionSettings.Default;
        return new LoginProtectionSettings
        {
            MaxAttempts = await config.GetIntAsync(LoginProtectionSettings.MaxAttemptsKey, defaults.MaxAttempts),
            LockoutMinutes = await config.GetIntAsync(LoginProtectionSettings.LockoutMinutesKey, defaults.LockoutMinutes),
            AccountWideFactor = await config.GetIntAsync(LoginProtectionSettings.AccountWideFactorKey, defaults.AccountWideFactor),
            IpMaxAttempts = await config.GetIntAsync(LoginProtectionSettings.IpMaxAttemptsKey, defaults.IpMaxAttempts),
            IpLockoutMinutes = await config.GetIntAsync(LoginProtectionSettings.IpLockoutMinutesKey, defaults.IpLockoutMinutes),
        }.Normalize();
    }
}
