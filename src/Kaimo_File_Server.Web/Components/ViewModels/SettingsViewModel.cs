using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class SettingsViewModel
{
    private readonly IConfigRepository _config;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(IConfigRepository config, ILogger<SettingsViewModel> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ── State ──

    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    // ── Language ──

    public string SelectedLanguage { get; set; } = "de";

    public static readonly Dictionary<string, string> AvailableLanguages = new()
    {
        ["de"] = "Deutsch",
        ["en"] = "English",
    };

    // ── Load ──

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            SelectedLanguage = await _config.GetStringAsync("app.language", "de");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            ErrorMessage = "Fehler beim Laden der Einstellungen.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Save Language ──

    public async Task<bool> SaveLanguageAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            await _config.SetAsync("app.language", SelectedLanguage);

            _logger.LogInformation("Language changed to '{Lang}'", SelectedLanguage);
            SuccessMessage = "Sprache gespeichert. Die Änderung wird beim nächsten Seitenaufruf wirksam.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save language setting");
            ErrorMessage = "Fehler beim Speichern der Sprache.";
            return false;
        }
    }

    /// <summary>Clears transient messages (call on tab switch).</summary>
    public void ClearMessages()
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }
}