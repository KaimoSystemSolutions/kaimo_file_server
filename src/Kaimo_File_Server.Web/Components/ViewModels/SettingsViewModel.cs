using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class SettingsViewModel
{
    private readonly IConfigRepository _config;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        IConfigRepository config,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<SettingsViewModel> logger)
    {
        _config = config;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
    }

    // ── State ──

    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    // ── Permissions ──

    /// <summary>May edit global settings such as the application language.</summary>
    public bool CanManageSettings { get; private set; }

    /// <summary>May start/stop data services (SMB, …).</summary>
    public bool CanManageDataServices { get; private set; }

    /// <summary>True if the user may access the settings page at all.</summary>
    public bool CanAccessPage => CanManageSettings || CanManageDataServices;

    // ── Language ──

    public string SelectedLanguage { get; set; } = "de";

    public static readonly Dictionary<string, string> AvailableLanguages = new()
    {
        ["de"] = "Deutsch",
        ["en"] = "English",
    };

    // ── Data Services (SMB) ──

    /// <summary>Desired state of the SMB service (config flag).</summary>
    public bool SmbEnabled { get; set; } = true;

    /// <summary>Last status the SMB host reported back, or "Unbekannt".</summary>
    public string SmbStatus { get; private set; } = "Unbekannt";

    // ── Load ──

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await LoadPermissionsAsync();

            if (CanManageSettings)
                SelectedLanguage = await _config.GetStringAsync("app.language", "de");

            if (CanManageDataServices)
            {
                SmbEnabled = await _config.GetBoolAsync(
                    DataServiceKeys.EnabledKey("smb"), fallback: true);
                // Status is written by the host process → read fresh, not cached.
                SmbStatus = await _config.GetFreshAsync(
                    DataServiceKeys.StatusKey("smb"), "Unbekannt");
            }
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

    private async Task LoadPermissionsAsync()
    {
        var actor = await BuildActorContextAsync();
        if (actor is null)
        {
            CanManageSettings = false;
            CanManageDataServices = false;
            return;
        }

        // Global settings are unrestricted/global by nature → require Global scope.
        CanManageSettings = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageSystemSettings);
        CanManageDataServices = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageDataServices);
    }

    private async Task<UserContext?> BuildActorContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    // ── Save Language ──

    public async Task<bool> SaveLanguageAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = "Keine Berechtigung zum Ändern der Einstellungen.";
            return false;
        }

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

    // ── Save Data Services ──

    public async Task<bool> SaveDataServicesAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageDataServices)
        {
            ErrorMessage = "Keine Berechtigung zum Verwalten der Datendienste.";
            return false;
        }

        try
        {
            await _config.SetAsync(DataServiceKeys.EnabledKey("smb"), SmbEnabled);

            _logger.LogInformation("SMB service desired state set to {Enabled}", SmbEnabled);
            SuccessMessage = SmbEnabled
                ? "SMB wird aktiviert. Die Änderung wird vom Dienst innerhalb weniger Sekunden übernommen."
                : "SMB wird deaktiviert. Die Änderung wird vom Dienst innerhalb weniger Sekunden übernommen.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save data service setting");
            ErrorMessage = "Fehler beim Speichern der Datendienst-Einstellung.";
            return false;
        }
    }

    /// <summary>Re-reads the reported status of the SMB service from the config store.</summary>
    public async Task RefreshDataServiceStatusAsync()
    {
        if (!CanManageDataServices) return;
        SmbStatus = await _config.GetFreshAsync(DataServiceKeys.StatusKey("smb"), "Unbekannt");
    }

    /// <summary>Clears transient messages (call on tab switch).</summary>
    public void ClearMessages()
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }
}
