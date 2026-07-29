using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.DynamicHelpers;
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
    private readonly IHttpsCertificateProvider _certProvider;
    private readonly ILoggingConfigStore _loggingStore;
    private readonly LoggingLevelConfigurationSource _loggingSource;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        IConfigRepository config,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ISystemInfoService sysInfo,
        ISearchAdminService searchAdmin,
        IHttpsCertificateProvider certProvider,
        ILoggingConfigStore loggingStore,
        LoggingLevelConfigurationSource loggingSource,
        ILogger<SettingsViewModel> logger)
    {
        _config = config;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _sysInfo = sysInfo;
        _searchAdmin = searchAdmin;
        _certProvider = certProvider;
        _loggingStore = loggingStore;
        _loggingSource = loggingSource;
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

    /// <summary>True if the user may access the settings page at all.</summary>
    public bool CanAccessPage => CanManageSettings || CanManageDataServices || CanManageCertificates;

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

    /// <summary>Progress of the last/running manual reindex.</summary>
    public ReindexProgress ReindexProgress { get; private set; } = ReindexProgress.Idle;

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

    public static int MinSessionRevalidationSeconds => SessionSecuritySettings.MinRevalidationSeconds;
    public static int MaxSessionRevalidationSeconds => SessionSecuritySettings.MaxRevalidationSeconds;

    // ── System Info (IP / Storage / RAM) ──

    public string HostName { get; private set; } = "";
    public IReadOnlyList<NetworkAddressInfo> NetworkAddresses { get; private set; } = [];
    public StorageUsageInfo? StorageUsage { get; private set; }
    public MemoryUsageInfo? MemoryUsage { get; private set; }

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
                SelectedLanguage = await _config.GetStringAsync("app.language", "de");
                CtxConfig = await _config.GetAsync(
                    ContextMenuConfig.ConfigKey, ContextMenuConfig.Default());
                PwPolicy = await _config.GetAsync(
                    PasswordPolicy.ConfigKey, PasswordPolicy.Default());
                SessionRevalidationSeconds = await _config.GetIntAsync(
                    SessionSecuritySettings.RevalidationSecondsKey,
                    SessionSecuritySettings.DefaultRevalidationSeconds);
                RefreshSystemInfo();
                await LoadSearchStateAsync();
                LogLevel = await _loggingStore.GetLevelAsync();
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
            return;
        }

        // Global settings are unrestricted/global by nature → require Global scope.
        CanManageSettings = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageSystemSettings);
        CanManageDataServices = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageDataServices);
        CanManageCertificates = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageCertificates);
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

    // ── Search engine (Elasticsearch) ──

    /// <summary>Reads the desired flag + live reachability of Elasticsearch.</summary>
    public async Task LoadSearchStateAsync()
    {
        if (!CanManageSettings) return;
        try
        {
            var state = await _searchAdmin.GetStateAsync();
            SearchEsEnabled = state.Enabled;
            SearchEsReachable = state.Reachable;
            SearchEsEffective = state.Effective;
            ReindexProgress = _searchAdmin.GetReindexProgress();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load search engine state");
        }
    }

    /// <summary>Re-reads only the reindex progress (cheap, in-memory).</summary>
    public Task RefreshReindexProgressAsync()
    {
        if (CanManageSettings)
            ReindexProgress = _searchAdmin.GetReindexProgress();
        return Task.CompletedTask;
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

    /// <summary>Triggers a manual full reindex of all files on disk.</summary>
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
            var started = await _searchAdmin.TryStartReindexAsync();
            ReindexProgress = _searchAdmin.GetReindexProgress();

            if (started)
                SuccessMessage = "Indexierung gestartet. Der Fortschritt wird unten angezeigt.";
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
        HostName = _sysInfo.HostName;
        NetworkAddresses = _sysInfo.GetNetworkAddresses();
        //StorageUsage = _sysInfo.GetStorageUsage();
        MemoryUsage = _sysInfo.GetMemoryUsage();
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

    // ── Context Menu (per-scope layout) ──

    /// <summary>The globally configured, per-scope context-menu layout (working copy).</summary>
    public ContextMenuConfig CtxConfig { get; private set; } = ContextMenuConfig.Default();

    /// <summary>The category currently being edited in the GUI.</summary>
    public ContextMenuScope SelectedScope { get; private set; } = ContextMenuScope.Folder;

    /// <summary>All editable categories.</summary>
    public static IReadOnlyList<ContextMenuScope> ContextScopes { get; } = Enum.GetValues<ContextMenuScope>();

    /// <summary>Commands assigned to the selected scope, in order.</summary>
    public IReadOnlyList<ContextCommand> AssignedCommands =>
        WorkingList()
            .Select(ContextCommandCatalog.ById)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    /// <summary>Valid commands for the selected scope that are not yet assigned.</summary>
    public IReadOnlyList<ContextCommand> AvailableCommands
    {
        get
        {
            var assigned = WorkingList();
            return ContextCommandCatalog.ForScope(SelectedScope)
                .Where(c => !assigned.Contains(c.Id))
                .ToList();
        }
    }

    /// <summary>
    /// All valid commands for the selected scope: assigned ones first (in their
    /// configured order), then the remaining unassigned ones. Drives the single
    /// checkbox list in the builder GUI.
    /// </summary>
    public IReadOnlyList<ContextCommand> ScopeCommandsOrdered
    {
        get
        {
            var assigned = AssignedCommands;
            var assignedIds = assigned.Select(c => c.Id).ToHashSet();
            var rest = ContextCommandCatalog.ForScope(SelectedScope)
                .Where(c => !assignedIds.Contains(c.Id));
            return assigned.Concat(rest).ToList();
        }
    }

    /// <summary>True if the command is currently part of the selected scope's menu.</summary>
    public bool IsAssigned(string id) => WorkingList().Contains(id);

    public void SelectScope(ContextMenuScope scope) => SelectedScope = scope;

    /// <summary>Adds the command if absent, removes it if present.</summary>
    public void ToggleCommand(string id)
    {
        var list = WorkingList();
        if (list.Contains(id)) list.Remove(id);
        else list.Add(id);
    }

    public void AddCommand(string id)
    {
        var list = WorkingList();
        if (!list.Contains(id)) list.Add(id);
    }

    public void RemoveCommand(string id) => WorkingList().Remove(id);

    public void MoveUp(string id)
    {
        var list = WorkingList();
        var i = list.IndexOf(id);
        if (i > 0) (list[i - 1], list[i]) = (list[i], list[i - 1]);
    }

    public void MoveDown(string id)
    {
        var list = WorkingList();
        var i = list.IndexOf(id);
        if (i >= 0 && i < list.Count - 1) (list[i + 1], list[i]) = (list[i], list[i + 1]);
    }

    public void ResetScopeToDefault()
        => CtxConfig.SetScope(SelectedScope, ContextMenuConfig.DefaultForScope(SelectedScope));

    public async Task<bool> SaveContextMenuAsync()
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

    /// <summary>Mutable, guaranteed-present command list for the selected scope.</summary>
    private List<string> WorkingList()
    {
        var key = SelectedScope.ToString();
        if (!CtxConfig.Menus.TryGetValue(key, out var list) || list is null)
        {
            list = ContextMenuConfig.DefaultForScope(SelectedScope);
            CtxConfig.Menus[key] = list;
        }
        return list;
    }

    /// <summary>Clears transient messages (call on tab switch).</summary>
    public void ClearMessages()
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }
}
