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
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Https;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class SettingsViewModel
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

    // ── Data Services (SMB) ──

    /// <summary>Desired state of the SMB service (config flag).</summary>
    public bool SmbEnabled { get; set; } = true;

    /// <summary>Last status the SMB host reported back, or "Unbekannt".</summary>
    public string SmbStatus { get; private set; } = "Unbekannt";

    /// <summary>
    /// SMB protocol/security options (dialect range, signing, encryption) —
    /// working copy edited on the Datendienste tab. Applied by the SMB host on
    /// the next (re)start of the service.
    /// </summary>
    public SmbProtocolSettings SmbProtocol { get; private set; } = SmbProtocolSettings.Default();

    /// <summary>All selectable SMB dialects with their display labels, oldest first.</summary>
    public static readonly IReadOnlyList<(SmbProtocolVersion Version, string Label)> SmbVersions =
    [
        (SmbProtocolVersion.Smb202, "SMB 2.0.2"),
        (SmbProtocolVersion.Smb210, "SMB 2.1"),
        (SmbProtocolVersion.Smb300, "SMB 3.0"),
        (SmbProtocolVersion.Smb302, "SMB 3.0.2"),
        (SmbProtocolVersion.Smb311, "SMB 3.1.1"),
    ];

    // ── Search engine (Elasticsearch) ──

    /// <summary>Desired state of Elasticsearch (config flag). When off, the
    /// filename fallback is used and nothing is indexed on write.</summary>
    public bool SearchEsEnabled { get; set; } = true;

    /// <summary>Whether Elasticsearch answered a ping (container present/reachable).</summary>
    public bool SearchEsReachable { get; private set; }

    /// <summary>True if ES is both enabled and reachable → actually in use.</summary>
    public bool SearchEsEffective { get; private set; }

    /// <summary>True while the search tab is actively probing Elasticsearch.</summary>
    public bool SearchStateLoading { get; private set; }

    /// <summary>Progress of the last/running manual reindex.</summary>
    public ReindexProgress ReindexProgress { get; private set; } = ReindexProgress.Idle;

    /// <summary>Enabled share names available for a scoped reindex.</summary>
    public List<string> ReindexShares { get; private set; } = new();

    /// <summary>Selected share for the next reindex; empty = all shares.</summary>
    public string ReindexSelectedShare { get; set; } = string.Empty;

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

    // ── HTTPS Certificate ──

    /// <summary>Certificate settings (mode, lifetime, CN, extra SANs) — working copy.</summary>
    public HttpsCertificateSettings CertSettings { get; private set; } = HttpsCertificateSettings.Default();

    /// <summary>Subject (CN) of the currently served certificate, or "—" when none.</summary>
    public string CertSubject { get; private set; } = "—";

    /// <summary>Issuer of the current certificate ("self" when self-signed).</summary>
    public string CertIssuer { get; private set; } = "—";

    /// <summary>Subject Alternative Names of the current certificate.</summary>
    public IReadOnlyList<string> CertSans { get; private set; } = [];

    public DateTime? CertIssuedUtc { get; private set; }
    public DateTime? CertExpiresUtc { get; private set; }
    public string CertThumbprint { get; private set; } = "";

    /// <summary>True if the current certificate is self-signed (subject == issuer).</summary>
    public bool CertIsSelfSigned { get; private set; }

    /// <summary>True while there is a certificate loaded to display.</summary>
    public bool HasCertificate { get; private set; }

    /// <summary>Days remaining until the current certificate expires (may be negative).</summary>
    public int? CertDaysRemaining =>
        CertExpiresUtc is { } exp ? (int)Math.Floor((exp - DateTime.UtcNow).TotalDays) : null;

    /// <summary>Working copy of the extra-SAN list as a single comma/newline separated string.</summary>
    public string CertAdditionalSansText { get; set; } = "";

    /// <summary>Working copy of the custom-certificate password entered in the upload form.</summary>
    public string CustomCertPassword { get; set; } = "";

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

    // ── Database backup ──

    /// <summary>Backup schedule/retention working copy edited on the Backup tab.</summary>
    public BackupSettings BackupSettings { get; private set; } = BackupSettings.Default();

    /// <summary>Existing backups on disk, newest first.</summary>
    public IReadOnlyList<BackupFileInfo> Backups { get; private set; } = [];

    /// <summary>True while a manual backup is being created.</summary>
    public bool BackupInProgress { get; private set; }

    public static int MinSessionRevalidationSeconds => SessionSecuritySettings.MinRevalidationSeconds;
    public static int MaxSessionRevalidationSeconds => SessionSecuritySettings.MaxRevalidationSeconds;

    // ── System Info (IP / Storage / RAM) ──

    public string HostName { get; private set; } = "";
    public IReadOnlyList<NetworkAddressInfo> NetworkAddresses { get; private set; } = [];
    public IReadOnlyList<StorageUsageInfo> StorageUsages { get; private set; } = [];
    public MemoryUsageInfo? MemoryUsage { get; private set; }

    /// <summary>
    /// Editable friendly names for the storage pools, keyed by the normalized pool
    /// path. Every displayed pool has an entry (blank = fall back to the path's
    /// final component). Persisted under <see cref="StoragePoolNaming.ConfigKey"/>.
    /// </summary>
    public Dictionary<string, string> PoolNames { get; private set; } = new();

    /// <summary>The label shown for a pool: the custom name if set, else derived.</summary>
    public string ResolvePoolName(string poolPath)
        => StoragePoolNaming.Resolve(PoolNames, poolPath);

    /// <summary>The normalized map key for a pool path (used for two-way binding).</summary>
    public static string PoolNameKey(string poolPath)
        => StoragePoolNaming.NormalizeKey(poolPath);

    /// <summary>The path-derived fallback name, shown as the input placeholder.</summary>
    public static string PoolDerivedName(string poolPath)
        => StoragePoolNaming.DerivedName(poolPath);

    /// <summary>The server's public IP, once resolved. Null = not (yet) resolved.</summary>
    public string? PublicIp { get; private set; }

    /// <summary>True while the public IP is being fetched from the echo service.</summary>
    public bool PublicIpLoading { get; private set; }

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

    // ── Save Data Services ──

    public async Task<bool> SaveDataServicesAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageDataServices)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionDataServices;
            return false;
        }

        try
        {
            await _config.SetAsync(DataServiceKeys.EnabledKey("smb"), SmbEnabled);

            _logger.LogInformation("SMB service desired state set to {Enabled}", SmbEnabled);
            SuccessMessage = SmbEnabled
                ? Resources.Web_Settings_SmbEnabled
                : Resources.Web_Settings_SmbDisabled;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save data service setting");
            ErrorMessage = Resources.Web_Settings_DataServiceSaveFailed;
            return false;
        }
    }

    /// <summary>Re-reads the reported status of the SMB service from the config store.</summary>
    public async Task RefreshDataServiceStatusAsync()
    {
        if (!CanManageDataServices) return;
        SmbStatus = await _config.GetFreshAsync(DataServiceKeys.StatusKey("smb"), "Unbekannt");
    }

    /// <summary>
    /// Persists the SMB protocol/security options (dialect range, signing,
    /// encryption). Changes are applied by the SMB host on the next (re)start of
    /// the service — a running server is not reconfigured live.
    /// </summary>
    public async Task<bool> SaveSmbProtocolAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageDataServices)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionDataServices;
            return false;
        }

        // Guard against an inverted range (min newer than max) that would leave
        // the server with no negotiable dialect.
        SmbProtocol.Normalize();

        try
        {
            await _config.SetAsync(SmbProtocolSettings.ConfigKey, SmbProtocol);

            _logger.LogInformation(
                "SMB protocol settings saved (Min={Min}, Max={Max}, Signing={Signing}, " +
                "Encryption={Encryption}, WsDiscovery={WsDiscovery}, Audit={Audit})",
                SmbProtocol.MinVersion, SmbProtocol.MaxVersion,
                SmbProtocol.RequireSigning, SmbProtocol.RequireEncryption,
                SmbProtocol.EnableWsDiscovery, SmbProtocol.EnableAuditLog);
            SuccessMessage = "SMB-Protokolleinstellungen gespeichert. " +
                "Sie werden beim nächsten Neustart des SMB-Dienstes wirksam.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save SMB protocol settings");
            ErrorMessage = Resources.Web_Settings_DataServiceSaveFailed;
            return false;
        }
    }

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

    // ── Database backup ──

    /// <summary>Re-reads the list of backups on disk (newest first).</summary>
    public void RefreshBackups() => Backups = _backupService.ListBackups();

    /// <summary>
    /// Builds a short-lived capability URL to download a backup. Authorization is
    /// enforced here (in the authenticated circuit); the token embeds the file
    /// name and the controller re-validates it. Returns "" without permission.
    /// </summary>
    public string CreateBackupDownloadUrl(string baseUri, string fileName)
    {
        if (!CanManageBackups)
            return "";
        var token = Uri.EscapeDataString(_backupDownloadTokens.Protect(fileName));
        return $"{baseUri.TrimEnd('/')}/api/database-backups/download?token={token}";
    }

    /// <summary>Saves the backup schedule/retention settings.</summary>
    public async Task<bool> SaveBackupAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageBackups)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        BackupSettings.Normalize();

        try
        {
            await _backupSettingsStore.SetAsync(BackupSettings);
            _logger.LogInformation(
                "Backup settings saved (enabled={Enabled}, window={Start}-{End}, keep={Count}/{Days}d).",
                BackupSettings.Enabled, BackupSettings.WindowStart, BackupSettings.WindowEnd,
                BackupSettings.RetentionCount, BackupSettings.RetentionDays);
            SuccessMessage = R("Web_Settings_Backup_Saved");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save backup settings");
            ErrorMessage = R("Web_Settings_Backup_SaveFailed");
            return false;
        }
    }

    /// <summary>Creates a manual backup immediately and refreshes the list.</summary>
    public async Task<bool> CreateBackupNowAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageBackups)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        BackupInProgress = true;
        try
        {
            var created = await _backupService.CreateBackupAsync(BackupTrigger.Manual);
            _logger.LogInformation("Manual database backup created: {FileName}", created.FileName);
            RefreshBackups();
            SuccessMessage = R("Web_Settings_Backup_Created");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual database backup failed");
            ErrorMessage = R("Web_Settings_Backup_CreateFailed");
            return false;
        }
        finally
        {
            BackupInProgress = false;
        }
    }

    // ── Search engine (Elasticsearch) ──

    /// <summary>Reads the desired flag + live reachability of Elasticsearch.</summary>
    public async Task LoadSearchStateAsync()
    {
        if (!CanManageSettings) return;
        SearchStateLoading = true;
        try
        {
            var state = await _searchAdmin.GetStateAsync();
            SearchEsEnabled = state.Enabled;
            SearchEsReachable = state.Reachable;
            SearchEsEffective = state.Effective;
            ReindexProgress = _searchAdmin.GetReindexProgress();

            ReindexShares = (await _shareRepo.GetAllEnabledAsync())
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Drop a stale selection if that share is gone.
            if (!ReindexShares.Contains(ReindexSelectedShare))
                ReindexSelectedShare = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load search engine state");
        }
        finally
        {
            SearchStateLoading = false;
        }
    }

    /// <summary>Re-reads only the reindex progress (cheap, in-memory).</summary>
    public Task RefreshReindexProgressAsync()
    {
        if (CanManageSettings)
            ReindexProgress = _searchAdmin.GetReindexProgress();
        return Task.CompletedTask;
    }

    /// <summary>Requests cancellation of the running reindex. The run stops at its
    /// next file/directory boundary; the poll reflects the canceled state.</summary>
    public void CancelReindex()
    {
        if (!CanManageSettings) return;
        _searchAdmin.CancelReindex();
        ReindexProgress = _searchAdmin.GetReindexProgress();
    }

    public async Task<bool> SaveSearchAsync()
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
            await _searchAdmin.SetElasticEnabledAsync(SearchEsEnabled);
            await LoadSearchStateAsync();

            _logger.LogInformation("Elasticsearch desired state set to {Enabled}", SearchEsEnabled);
            SuccessMessage = SearchEsEnabled
                ? "Elasticsearch aktiviert. Wird verwendet, sobald der Container erreichbar ist."
                : "Elasticsearch deaktiviert. Es wird die Dateinamen-Suche verwendet und nicht mehr indexiert.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save search engine setting");
            ErrorMessage = "Such-Einstellung konnte nicht gespeichert werden.";
            return false;
        }
    }

    /// <summary>Triggers a manual reindex — of all files on disk, or just the
    /// files in <see cref="ReindexSelectedShare"/> when one is selected.</summary>
    public async Task StartReindexAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return;
        }

        try
        {
            var share = string.IsNullOrEmpty(ReindexSelectedShare) ? null : ReindexSelectedShare;
            var started = await _searchAdmin.TryStartReindexAsync(share);
            ReindexProgress = _searchAdmin.GetReindexProgress();

            if (started)
                SuccessMessage = share is null
                    ? "Indexierung gestartet. Der Fortschritt wird unten angezeigt."
                    : $"Indexierung für Share '{share}' gestartet. Der Fortschritt wird unten angezeigt.";
            else if (ReindexProgress.Running)
                ErrorMessage = "Es läuft bereits eine Indexierung.";
            else
                ErrorMessage = "Indexierung nicht möglich: Elasticsearch ist deaktiviert oder nicht erreichbar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start reindex");
            ErrorMessage = "Indexierung konnte nicht gestartet werden.";
        }
    }

    // ── HTTPS Certificate ──

    /// <summary>Reads the current certificate + settings into the view state.</summary>
    public async Task LoadCertificateStateAsync()
    {
        if (!CanManageCertificates) return;

        CertSettings = await _config.GetAsync(
            HttpsCertificateSettings.ConfigKey, HttpsCertificateSettings.Default());
        CertSettings.Normalize();
        CertAdditionalSansText = string.Join(", ", CertSettings.AdditionalSans);

        var cert = _certProvider.Current;
        HasCertificate = cert is not null;
        if (cert is null)
        {
            CertSubject = CertIssuer = "—";
            CertSans = [];
            CertIssuedUtc = CertExpiresUtc = null;
            CertThumbprint = "";
            CertIsSelfSigned = false;
            return;
        }

        CertSubject = cert.Subject;
        CertIssuer = cert.Issuer;
        CertIsSelfSigned = string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal);
        CertIssuedUtc = cert.NotBefore.ToUniversalTime();
        CertExpiresUtc = cert.NotAfter.ToUniversalTime();
        CertThumbprint = cert.Thumbprint;
        CertSans = ReadSans(cert);
    }

    /// <summary>Extracts the DNS/IP Subject Alternative Names for display.</summary>
    private static IReadOnlyList<string> ReadSans(
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue; // subjectAltName
            // FormatValue gives a readable "DNS Name=…, IP Address=…" list.
            return ext.Format(false)
                .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        return [];
    }

    /// <summary>Persists lifetime/CN/SAN settings and reissues a self-signed cert with them.</summary>
    public async Task<bool> SaveCertificateSettingsAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageCertificates)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        CertSettings.AdditionalSans = CertAdditionalSansText
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        CertSettings.Normalize();

        try
        {
            await _config.SetAsync(HttpsCertificateSettings.ConfigKey, CertSettings);
            // Reissue with the new lifetime/CN/SANs so the change is visible immediately.
            await _certProvider.RegenerateAsync();
            await LoadCertificateStateAsync();

            _logger.LogInformation(
                "HTTPS certificate settings saved (LifetimeDays={Days}) and certificate reissued",
                CertSettings.LifetimeDays);
            SuccessMessage = "Zertifikatseinstellungen gespeichert und neues Zertifikat erzeugt.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save certificate settings");
            ErrorMessage = "Zertifikatseinstellungen konnten nicht gespeichert werden.";
            return false;
        }
    }

    /// <summary>Forces a fresh self-signed certificate right now.</summary>
    public async Task<bool> RegenerateCertificateNowAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageCertificates)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _certProvider.RegenerateAsync();
            await LoadCertificateStateAsync();
            SuccessMessage = "Neues selbstsigniertes Zertifikat erzeugt. Es wird ohne Neustart ausgeliefert.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate certificate");
            ErrorMessage = "Zertifikat konnte nicht neu erzeugt werden.";
            return false;
        }
    }

    /// <summary>
    /// Replaces the served certificate with an admin-supplied certificate. Accepts
    /// PKCS#12 (.pfx/.p12) as well as PEM bundles (.pem/.crt/.cer) with a private key; the
    /// format is auto-detected in the provider. When the private key is a separate file,
    /// pass it via <paramref name="keyBytes"/>.
    /// </summary>
    public async Task<bool> ImportCustomCertificateAsync(byte[] certBytes, byte[]? keyBytes = null)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageCertificates)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        if (certBytes.Length == 0)
        {
            ErrorMessage = "Bitte zuerst eine Zertifikatsdatei (.pfx/.p12 oder .pem/.crt/.cer) auswählen.";
            return false;
        }

        try
        {
            await _certProvider.ImportCustomAsync(
                certBytes,
                string.IsNullOrEmpty(CustomCertPassword) ? null : CustomCertPassword,
                keyBytes);
            CustomCertPassword = "";
            await LoadCertificateStateAsync();
            SuccessMessage = "Eigenes Zertifikat übernommen. Die automatische Erneuerung ist dafür pausiert.";
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import custom certificate");
            ErrorMessage = "Eigenes Zertifikat konnte nicht übernommen werden.";
            return false;
        }
    }

    /// <summary>The public certificate as a base64 DER string, for a client-side download link.</summary>
    public string? GetPublicCertDerBase64()
    {
        var der = _certProvider.GetPublicCertDer();
        return der is null ? null : Convert.ToBase64String(der);
    }

    /// <summary>The public certificate as a base64 PEM string, for a client-side download link.</summary>
    public string? GetPublicCertPemBase64()
    {
        var pem = _certProvider.GetPublicCertPem();
        return pem is null ? null : Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem));
    }

    // ── System Info (IP / Storage / RAM) ──

    /// <summary>Re-samples host network, storage and memory information (local, instant).</summary>
    public void RefreshSystemInfo()
    {
        if (!CanManageSettings) return;
        RefreshNetworkInfo();
        RefreshStorageInfo();
        RefreshMemoryInfo();
    }

    public void RefreshNetworkInfo()
    {
        if (!CanManageSettings) return;
        HostName = _sysInfo.HostName;
        NetworkAddresses = _sysInfo.GetNetworkAddresses();
    }

    public void RefreshStorageInfo()
    {
        if (!CanManageSettings) return;
        StorageUsages = _sysInfo.GetStorageUsage();

        // Keep an editable entry for every visible pool so the rename inputs can
        // bind to the map; preserve any unsaved edits already typed.
        foreach (var usage in StorageUsages)
        {
            var key = StoragePoolNaming.NormalizeKey(usage.StoragePath);
            if (!PoolNames.ContainsKey(key))
                PoolNames[key] = "";
        }
    }

    public void RefreshMemoryInfo()
    {
        if (!CanManageSettings) return;
        MemoryUsage = _sysInfo.GetMemoryUsage();
    }

    /// <summary>
    /// Loads the persisted custom pool names and ensures every currently visible
    /// pool has an (at least blank) entry so the editor can bind to it.
    /// </summary>
    private async Task LoadPoolNamesAsync()
    {
        PoolNames = await _config.GetAsync(
            StoragePoolNaming.ConfigKey, new Dictionary<string, string>());

        foreach (var usage in StorageUsages)
        {
            var key = StoragePoolNaming.NormalizeKey(usage.StoragePath);
            if (!PoolNames.ContainsKey(key))
                PoolNames[key] = "";
        }
    }

    /// <summary>
    /// Persists the custom pool names. Blank entries are dropped so a pool falls
    /// back to its path-derived name. The underlying pool paths are never changed.
    /// </summary>
    public async Task<bool> SavePoolNamesAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        var toStore = PoolNames
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());

        try
        {
            await _config.SetAsync(StoragePoolNaming.ConfigKey, toStore);
            _logger.LogInformation(
                "Storage pool names saved ({Count} custom name(s))", toStore.Count);
            SuccessMessage = Resources.Web_Settings_Storage_PoolNamesSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save storage pool names");
            ErrorMessage = Resources.Web_Settings_Storage_PoolNamesSaveFailed;
            return false;
        }
    }

    /// <summary>
    /// Resolves the server's public IP via an outbound call. No-op if already
    /// resolved unless <paramref name="force"/> is set. Sets <see cref="PublicIpLoading"/>
    /// across the await so the UI can show a spinner.
    /// </summary>
    public async Task LoadPublicIpAsync(bool force = false)
    {
        if (!CanManageSettings) return;
        if (PublicIp is not null && !force) return;

        PublicIpLoading = true;
        PublicIp = null;
        try
        {
            PublicIp = await _sysInfo.GetPublicIpAsync();
        }
        finally
        {
            PublicIpLoading = false;
        }
    }

    /// <summary>Formats a byte count as a human-readable size (e.g. "1.4 GB").</summary>
    public string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    // ── Context Menu (matrix editor) ──
    //
    // The editor works in two separate concerns so that toggling membership never
    // reorders anything (the old single-list editor conflated the two, which made
    // rows jump on every click):
    //   • membership — which command appears in which category (the matrix), held
    //     in _members as "commandId::Scope" keys;
    //   • order — one global command order (_order) that every category filters.
    // On save each scope's persisted menu becomes  _order ∩ members(scope), so the
    // renderer keeps consuming ContextMenuConfig.ForScope unchanged.

    /// <summary>The persisted layout (source for load, target for save).</summary>
    public ContextMenuConfig CtxConfig { get; private set; } = ContextMenuConfig.Default();

    /// <summary>All categories = the matrix columns.</summary>
    public static IReadOnlyList<ContextMenuScope> ContextScopes { get; } = Enum.GetValues<ContextMenuScope>();

    /// <summary>The category shown in the live preview (does not affect editing).</summary>
    public ContextMenuScope PreviewScope { get; private set; } = ContextMenuScope.Folder;

    // "commandId::Scope" for every assigned cell.
    private readonly HashSet<string> _members = new();
    // The single global command order (superset of everything shown as matrix rows).
    private List<string> _order = new();

    private static string MKey(string commandId, ContextMenuScope scope) => $"{commandId}::{scope}";

    /// <summary>
    /// Projects the persisted <see cref="CtxConfig"/> into the editor's membership +
    /// order working state. Call after (re)loading or resetting CtxConfig.
    /// </summary>
    public void RebuildContextEditorState()
    {
        _members.Clear();
        foreach (var scope in ContextScopes)
            foreach (var id in CtxConfig.ForScope(scope))
                if (ContextCommandCatalog.ById(id)?.IsValidFor(scope) == true)
                    _members.Add(MKey(id, scope));

        // Start from the stored (or default) order, then reconcile against the catalog
        // so unknown ids are dropped and newly added commands still show up.
        _order = new List<string>(CtxConfig.Order ?? ContextMenuConfig.DefaultOrder);
        _order.RemoveAll(id => ContextCommandCatalog.ById(id) is null);
        foreach (var c in ContextCommandCatalog.All)
            if (!_order.Contains(c.Id))
                _order.Add(c.Id);

        // General items (new folder / refresh) are pinned to the bottom of the menu.
        // OrderBy is a stable sort, so relative order within each group is preserved.
        _order = _order.OrderBy(id => IsGeneralCmd(id) ? 1 : 0).ToList();
    }

    private static bool IsGeneralCmd(string id) => ContextCommandCatalog.GeneralIds.Contains(id);

    /// <summary>Command rows for the matrix, in the global order.</summary>
    public IReadOnlyList<ContextCommand> OrderedCommands =>
        _order.Select(ContextCommandCatalog.ById).Where(c => c is not null).Select(c => c!).ToList();

    /// <summary>True if <paramref name="commandId"/> may appear in <paramref name="scope"/>.</summary>
    public bool IsValidCell(string commandId, ContextMenuScope scope)
        => ContextCommandCatalog.ById(commandId)?.IsValidFor(scope) == true;

    /// <summary>True if the cell is currently ticked.</summary>
    public bool IsCellOn(string commandId, ContextMenuScope scope)
        => _members.Contains(MKey(commandId, scope));

    /// <summary>Toggle a single cell (no-op for invalid combinations).</summary>
    public void ToggleCell(string commandId, ContextMenuScope scope)
    {
        if (!IsValidCell(commandId, scope)) return;
        var key = MKey(commandId, scope);
        if (!_members.Remove(key)) _members.Add(key);
    }

    /// <summary>Toggle a command across every category it is valid for (row action).</summary>
    public void ToggleRow(string commandId)
    {
        var scopes = ContextScopes.Where(s => IsValidCell(commandId, s)).ToList();
        var allOn = scopes.All(s => _members.Contains(MKey(commandId, s)));
        foreach (var s in scopes)
        {
            var key = MKey(commandId, s);
            if (allOn) _members.Remove(key); else _members.Add(key);
        }
    }

    /// <summary>Toggle every valid command for one category (column action).</summary>
    public void ToggleColumn(ContextMenuScope scope)
    {
        var cmds = ContextCommandCatalog.ForScope(scope).Select(c => c.Id).ToList();
        var allOn = cmds.All(id => _members.Contains(MKey(id, scope)));
        foreach (var id in cmds)
        {
            var key = MKey(id, scope);
            if (allOn) _members.Remove(key); else _members.Add(key);
        }
    }

    public void SelectPreviewScope(ContextMenuScope scope) => PreviewScope = scope;

    /// <summary>Move <paramref name="commandId"/> in the global order to sit before
    /// <paramref name="targetId"/> (drag-and-drop reorder).</summary>
    public void ReorderCommand(string commandId, string targetId)
    {
        if (commandId == targetId) return;
        // Keep general and main items in separate blocks: only reorder within a group.
        if (IsGeneralCmd(commandId) != IsGeneralCmd(targetId)) return;
        var from = _order.IndexOf(commandId);
        var to = _order.IndexOf(targetId);
        if (from < 0 || to < 0) return;
        _order.RemoveAt(from);
        if (from < to) to--;
        _order.Insert(to, commandId);
    }

    public void MoveCommandUp(string commandId)
    {
        var i = _order.IndexOf(commandId);
        // Block swapping across the main/general boundary.
        if (i > 0 && IsGeneralCmd(commandId) == IsGeneralCmd(_order[i - 1]))
            (_order[i - 1], _order[i]) = (_order[i], _order[i - 1]);
    }

    public void MoveCommandDown(string commandId)
    {
        var i = _order.IndexOf(commandId);
        if (i >= 0 && i < _order.Count - 1 && IsGeneralCmd(commandId) == IsGeneralCmd(_order[i + 1]))
            (_order[i + 1], _order[i]) = (_order[i], _order[i + 1]);
    }

    /// <summary>The commands that would render for <see cref="PreviewScope"/>, in order.</summary>
    public IReadOnlyList<ContextCommand> PreviewCommands =>
        _order.Where(id => _members.Contains(MKey(id, PreviewScope)) && IsValidCell(id, PreviewScope))
              .Select(ContextCommandCatalog.ById).Where(c => c is not null).Select(c => c!).ToList();

    public void ResetContextToDefault()
    {
        CtxConfig = ContextMenuConfig.Default();
        RebuildContextEditorState();
    }

    public async Task<bool> SaveContextMenuAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Fold the editor working state back into the persisted, per-scope format.
        CtxConfig.Order = new List<string>(_order);
        foreach (var scope in ContextScopes)
        {
            var ids = _order
                .Where(id => _members.Contains(MKey(id, scope)) && IsValidCell(id, scope))
                .ToList();
            CtxConfig.Menus[scope.ToString()] = ids;
        }

        try
        {
            await _config.SetAsync(ContextMenuConfig.ConfigKey, CtxConfig);
            _logger.LogInformation("Context menu layout saved");
            SuccessMessage = Resources.Web_Settings_CtxSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save context menu layout");
            ErrorMessage = Resources.Web_Settings_CtxSaveFailed;
            return false;
        }
    }

    /// <summary>Clears transient messages (call on tab switch).</summary>
    public void ClearMessages()
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
