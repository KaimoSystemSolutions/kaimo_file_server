using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Security-related settings: password policy, session revalidation, Cloud Access runtime.</summary>
public partial class SettingsViewModel
{
    // ── Password Policy ──

    /// <summary>Globally enforced password requirements (working copy).</summary>
    public PasswordPolicy PwPolicy { get; private set; } = PasswordPolicy.Default();

    // ── Session Security ──

    /// <summary>
    /// How often (seconds) an active web session re-checks the account's enabled state, so a
    /// disabled/removed user is signed out within this window (working copy).
    /// </summary>
    public int SessionRevalidationSeconds { get; set; } = SessionSecuritySettings.DefaultRevalidationSeconds;

    /// <summary>Global Cloud Access runtime settings edited in the Settings UI.</summary>
    public CloudAccessRuntimeSettings CloudAccessSettings { get; private set; }
        = CloudAccessRuntimeSettings.Default();

    public static int MinSessionRevalidationSeconds => SessionSecuritySettings.MinRevalidationSeconds;
    public static int MaxSessionRevalidationSeconds => SessionSecuritySettings.MaxRevalidationSeconds;

    // ── Save Password Policy ──

    public async Task<bool> SavePasswordPolicyAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Guard against a nonsensical minimum that would lock everyone out of
        // creating passwords; clamp to a sane range.
        PwPolicy.MinLength = Math.Clamp(PwPolicy.MinLength, 1, 128);

        try
        {
            await _config.SetAsync(PasswordPolicy.ConfigKey, PwPolicy);
            _logger.LogInformation("Password policy saved (MinLength={Min})", PwPolicy.MinLength);
            SuccessMessage = Resources.Web_Settings_PwPolicySaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save password policy");
            ErrorMessage = Resources.Web_Settings_PwPolicySaveFailed;
            return false;
        }
    }

    // ── Save Session Security ──

    public async Task<bool> SaveSessionSecurityAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Clamp so a nonsensical value can neither hammer the DB nor make the check meaningless.
        SessionRevalidationSeconds =
            SessionSecuritySettings.ClampRevalidationSeconds(SessionRevalidationSeconds);

        try
        {
            await _config.SetAsync(
                SessionSecuritySettings.RevalidationSecondsKey, SessionRevalidationSeconds);
            _logger.LogInformation(
                "Session revalidation interval set to {Seconds}s", SessionRevalidationSeconds);
            SuccessMessage = $"Sitzungsprüfung gespeichert (alle {SessionRevalidationSeconds}s).";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save session security setting");
            ErrorMessage = "Sitzungs-Einstellung konnte nicht gespeichert werden.";
            return false;
        }
    }

    public async Task<bool> SaveCloudAccessSettingsAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        CloudAccessSettings.Normalize();
        try
        {
            await _cloudAccessSettingsStore.SetAsync(CloudAccessSettings);
            _logger.LogInformation(
                "Cloud Access directory cache TTL set to {Seconds}s",
                CloudAccessSettings.DirectoryCacheSeconds);
            SuccessMessage = R("Web_Settings_CloudAccess_CacheSaved");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Cloud Access settings");
            ErrorMessage = R("Web_Settings_CloudAccess_CacheSaveFailed");
            return false;
        }
    }
}
