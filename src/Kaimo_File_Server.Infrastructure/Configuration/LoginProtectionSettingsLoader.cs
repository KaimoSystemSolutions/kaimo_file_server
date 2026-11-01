using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Infrastructure.Configuration;

/// <summary>Reads <see cref="LoginProtectionSettings"/> from the config store.</summary>
public static class LoginProtectionSettingsLoader
{
    /// <summary>
    /// Loads every login-protection value (missing keys use the defaults) and normalizes the result.
    /// <paramref name="fresh"/> bypasses the config cache, for processes that do not write the
    /// values themselves (the SMB bridge would otherwise serve web changes up to 10 minutes late).
    /// </summary>
    public static async Task<LoginProtectionSettings> LoadLoginProtectionAsync(this IConfigRepository config, bool fresh = false)
    {
        var defaults = LoginProtectionSettings.Default;
        Task<int> Int(string key, int fallback) => fresh ? config.GetFreshAsync(key, fallback) : config.GetIntAsync(key, fallback);
        Task<bool> Bool(string key, bool fallback) => fresh ? config.GetFreshAsync(key, fallback) : config.GetBoolAsync(key, fallback);
        return new LoginProtectionSettings
        {
            MaxAttempts = await Int(LoginProtectionSettings.MaxAttemptsKey, defaults.MaxAttempts),
            LockoutMinutes = await Int(LoginProtectionSettings.LockoutMinutesKey, defaults.LockoutMinutes),
            AccountWideFactor = await Int(LoginProtectionSettings.AccountWideFactorKey, defaults.AccountWideFactor),
            IpMaxAttempts = await Int(LoginProtectionSettings.IpMaxAttemptsKey, defaults.IpMaxAttempts),
            IpLockoutMinutes = await Int(LoginProtectionSettings.IpLockoutMinutesKey, defaults.IpLockoutMinutes),
            ShareLinkMaxAttemptsPerAddress = await Int(LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey, defaults.ShareLinkMaxAttemptsPerAddress),
            ShareLinkMaxAttemptsPerLink = await Int(LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey, defaults.ShareLinkMaxAttemptsPerLink),
            ShareLinkLockoutMinutes = await Int(LoginProtectionSettings.ShareLinkLockoutMinutesKey, defaults.ShareLinkLockoutMinutes),
            SmbLockoutEnabled = await Bool(LoginProtectionSettings.SmbLockoutEnabledKey, defaults.SmbLockoutEnabled),
            SmbLockoutAttempts = await Int(LoginProtectionSettings.SmbLockoutAttemptsKey, defaults.SmbLockoutAttempts),
            SmbLockoutMinutes = await Int(LoginProtectionSettings.SmbLockoutMinutesKey, defaults.SmbLockoutMinutes),
        }.Normalize();
    }
}
