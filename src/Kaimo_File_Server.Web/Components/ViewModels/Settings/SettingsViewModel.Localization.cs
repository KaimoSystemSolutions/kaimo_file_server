using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Language and date/time display-format settings.</summary>
public partial class SettingsViewModel
{
    // ── Language ──

    public string SelectedLanguage { get; set; } = "de";

    public static readonly Dictionary<string, string> AvailableLanguages = new()
    {
        ["de"] = "Deutsch",
        ["en"] = "English",
    };

    // ── Date display format ──

    /// <summary>Selected global date-display format (config key <c>display.dateformat</c>).</summary>
    public string SelectedDateFormat { get; set; } = DateFormatService.DefaultFormat;

    /// <summary>Available date formats: key → resx label key for the option.</summary>
    public static readonly Dictionary<string, string> AvailableDateFormats = new()
    {
        ["iso"] = "Web_Settings_DateFormat_Iso",
        ["european"] = "Web_Settings_DateFormat_European",
        ["american"] = "Web_Settings_DateFormat_American",
    };

    /// <summary>Selected global time-display format (config key <c>display.timeformat</c>).</summary>
    public string SelectedTimeFormat { get; set; } = DateFormatService.DefaultTimeFormat;

    /// <summary>Available time formats: key → resx label key for the option.</summary>
    public static readonly Dictionary<string, string> AvailableTimeFormats = new()
    {
        ["24h"] = "Web_Settings_TimeFormat_24h",
        ["12h"] = "Web_Settings_TimeFormat_12h",
    };

    // ── Save Language ──

    public async Task<bool> SaveLanguageAsync()
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
            await _config.SetAsync("app.language", SelectedLanguage);
            ConfigLocalizationMiddleware.ApplyDefaultCulture(SelectedLanguage);

            _logger.LogInformation("Language changed to '{Lang}'", SelectedLanguage);
            SuccessMessage = Resources.Web_Settings_LanguageSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save language setting");
            ErrorMessage = Resources.Web_Settings_LanguageSaveFailed;
            return false;
        }
    }

    // ── Save Date Format ──

    public async Task<bool> SaveDateFormatAsync()
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
            await _config.SetAsync(DateFormatService.ConfigKey, SelectedDateFormat);
            await _config.SetAsync(DateFormatService.TimeConfigKey, SelectedTimeFormat);

            _logger.LogInformation(
                "Date/time display format changed to '{Format}' / '{TimeFormat}'",
                SelectedDateFormat, SelectedTimeFormat);
            SuccessMessage = Resources.Web_Settings_DateFormatSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save date format setting");
            ErrorMessage = Resources.Web_Settings_DateFormatSaveFailed;
            return false;
        }
    }
}
