using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Https;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backing view model for the Settings page. The individual settings sections
/// (localization, data services, logging, search, security, certificate, backup,
/// system info, context menu) live in the <c>SettingsViewModel.*.cs</c> partials;
/// this file holds the shared state, permission gate and the <see cref="LoadAsync"/>
/// orchestrator that populates each section a user is allowed to see.
/// </summary>
public partial class SettingsViewModel
{
    private readonly IConfigRepository _config;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ISystemInfoService _sysInfo;
    private readonly ISearchAdminService _searchAdmin;
    private readonly IShareRepository _shareRepo;
    private readonly IHttpsCertificateProvider _certProvider;
    private readonly ILoggingConfigStore _loggingStore;
    private readonly LoggingLevelConfigurationSource _loggingSource;
    private readonly ICloudAccessSettingsStore _cloudAccessSettingsStore;
    private readonly IBackupSettingsStore _backupSettingsStore;
    private readonly IDatabaseBackupService _backupService;
    private readonly BackupDownloadTokenService _backupDownloadTokens;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        IConfigRepository config,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ISystemInfoService sysInfo,
        ISearchAdminService searchAdmin,
        IShareRepository shareRepo,
        IHttpsCertificateProvider certProvider,
        ILoggingConfigStore loggingStore,
        LoggingLevelConfigurationSource loggingSource,
        ICloudAccessSettingsStore cloudAccessSettingsStore,
        IBackupSettingsStore backupSettingsStore,
        IDatabaseBackupService backupService,
        BackupDownloadTokenService backupDownloadTokens,
        ILogger<SettingsViewModel> logger)
    {
        _config = config;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _sysInfo = sysInfo;
        _searchAdmin = searchAdmin;
        _shareRepo = shareRepo;
        _certProvider = certProvider;
        _loggingStore = loggingStore;
        _loggingSource = loggingSource;
        _cloudAccessSettingsStore = cloudAccessSettingsStore;
        _backupSettingsStore = backupSettingsStore;
        _backupService = backupService;
        _backupDownloadTokens = backupDownloadTokens;
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

    /// <summary>May view/download/replace the HTTPS server certificate.</summary>
    public bool CanManageCertificates { get; private set; }

    /// <summary>May view and download retained service logs.</summary>
    public bool CanViewLogs { get; private set; }

    /// <summary>May configure, create and download database backups.</summary>
    public bool CanManageBackups { get; private set; }

    /// <summary>May review and revoke users' registered client devices.</summary>
    public bool CanManageClientDevices { get; private set; }

    /// <summary>True if the user may access the settings page at all.</summary>
    public bool CanAccessPage => CanManageSettings || CanManageDataServices || CanManageCertificates || CanViewLogs || CanManageBackups || CanManageClientDevices;

    // ── Load ──

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await LoadPermissionsAsync();

            if (CanManageSettings)
            {
                var languageTask = _config.GetStringAsync("app.language", "de");
                var dateFormatTask = _config.GetStringAsync(
                    DateFormatService.ConfigKey, DateFormatService.DefaultFormat);
                var timeFormatTask = _config.GetStringAsync(
                    DateFormatService.TimeConfigKey, DateFormatService.DefaultTimeFormat);
                var contextMenuTask = _config.GetAsync(
                    ContextMenuConfig.ConfigKey, ContextMenuConfig.Default());
                var passwordPolicyTask = _config.GetAsync(
                    PasswordPolicy.ConfigKey, PasswordPolicy.Default());
                var sessionSecurityTask = _config.GetIntAsync(
                    SessionSecuritySettings.RevalidationSecondsKey,
                    SessionSecuritySettings.DefaultRevalidationSeconds);
                var loggingTask = _loggingStore.GetLevelAsync();
                var cloudAccessSettingsTask = _cloudAccessSettingsStore.GetAsync();

                await Task.WhenAll(
                    languageTask,
                    dateFormatTask,
                    timeFormatTask,
                    contextMenuTask,
                    passwordPolicyTask,
                    sessionSecurityTask,
                    loggingTask,
                    cloudAccessSettingsTask);

                SelectedLanguage = await languageTask;
                SelectedDateFormat = await dateFormatTask;
                SelectedTimeFormat = await timeFormatTask;
                CtxConfig = await contextMenuTask;
                RebuildContextEditorState();
                PwPolicy = await passwordPolicyTask;
                SessionRevalidationSeconds = await sessionSecurityTask;
                LogLevel = await loggingTask;
                CloudAccessSettings = await cloudAccessSettingsTask;
                RefreshSystemInfo();
                await LoadPoolNamesAsync();
            }

            if (CanManageBackups)
            {
                BackupSettings = await _backupSettingsStore.GetAsync();
                RefreshBackups();
            }

            if (CanManageCertificates)
                await LoadCertificateStateAsync();

            if (CanManageDataServices)
            {
                SmbEnabled = await _config.GetBoolAsync(
                    DataServiceKeys.EnabledKey("smb"), fallback: true);
                SmbProtocol = await _config.GetAsync(
                    SmbProtocolSettings.ConfigKey, SmbProtocolSettings.Default());
                SmbProtocol.Normalize();
                // Status is written by the host process → read fresh, not cached.
                SmbStatus = await _config.GetFreshAsync(
                    DataServiceKeys.StatusKey("smb"), "Unbekannt");

                WebDavEnabled = await _config.GetBoolAsync(WebDavOptions.EnabledKey, false);
                WebDavRequireHttps = await _config.GetBoolAsync(WebDavOptions.RequireHttpsKey, true);
                WebDavStatus = await _config.GetFreshAsync(WebDavOptions.StatusKey, "Unbekannt");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            ErrorMessage = Resources.Web_Settings_LoadFailed;
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
            CanManageCertificates = false;
            CanViewLogs = false;
            CanManageBackups = false;
            CanManageClientDevices = false;
            return;
        }

        // Resolve the complete global permission mask once. Three individual
        // checks would repeat the same assignment and role lookups.
        var permissions = await _mgmtAuth.GetEffectivePermissionsAtAsync(
            actor, ScopeType.Global, Guid.Empty);
        CanManageSettings = permissions.HasFlag(ManagementPermission.ManageSystemSettings);
        CanManageDataServices = permissions.HasFlag(ManagementPermission.ManageDataServices);
        CanManageCertificates = permissions.HasFlag(ManagementPermission.ManageCertificates);
        CanViewLogs = permissions.HasFlag(ManagementPermission.ViewSystemLogs);
        CanManageBackups = permissions.HasFlag(ManagementPermission.ManageBackups);
        CanManageClientDevices = permissions.HasFlag(ManagementPermission.ManageClientDevices);
    }

    private async Task<UserContext?> BuildActorContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    /// <summary>Clears transient messages (call on tab switch).</summary>
    public void ClearMessages()
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
