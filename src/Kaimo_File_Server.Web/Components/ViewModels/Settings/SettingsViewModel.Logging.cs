using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Global application log-level setting.</summary>
public partial class SettingsViewModel
{
    // ── Logging (global log level) ──

    /// <summary>The single global application log level (working copy). One of <see cref="LogLevels"/>.</summary>
    public string LogLevel { get; set; } = LoggingConfigKeys.DefaultLevel;

    /// <summary>
    /// Active highest-priority process override. The DB value can still be edited
    /// and becomes effective after the environment override is removed.
    /// </summary>
    public string? LogLevelEnvironmentOverride => _loggingSource.ManualOverrideLevel;

    /// <summary>Selectable log levels, most→least verbose.</summary>
    public static IReadOnlyList<string> LogLevels => LoggingConfigKeys.AllowedLevels;

    // ── Save Logging ──

    /// <summary>
    /// Persists the global log level and applies it immediately in this (Web) process.
    /// Other processes (the SMB host) pick it up through their own reloader within one
    /// poll interval. Mirrors the other save methods' permission + messaging pattern.
    /// </summary>
    public async Task<bool> SaveLoggingAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _loggingStore.SetLevelAsync(LogLevel);

            // Apply right away in this process so the change is visible without waiting
            // for the reloader tick; cross-process propagation happens via the reloaders.
            _loggingSource.SetLevel(LogLevel);

            _logger.LogInformation("Global log level set to {Level}", LogLevel);
            SuccessMessage = Resources.Web_Settings_Logging_Saved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save log level setting");
            ErrorMessage = Resources.Web_Settings_DataServiceSaveFailed;
            return false;
        }
    }
}
