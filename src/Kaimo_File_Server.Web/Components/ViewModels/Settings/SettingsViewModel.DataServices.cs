using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Web.Controllers.WebDav;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Data-service settings: SMB (dialects/security) and WebDAV.</summary>
public partial class SettingsViewModel
{
    // ── Data Services (SMB) ──

    /// <summary>Desired state of the SMB service (config flag).</summary>
    public bool SmbEnabled { get; set; } = true;

    /// <summary>Last status the SMB host reported back, or "Unbekannt".</summary>
    public string SmbStatus { get; private set; } = "Unbekannt";

    /// <summary>
    /// SMB protocol/security options (dialect range, signing, encryption) —
    /// working copy edited on the Datendienste tab. Applied within seconds to new
    /// SMB connections.
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

    // ── Data Services (WebDAV) ──

    /// <summary>Desired state of the WebDAV service (config flag; opt-in, defaults off).</summary>
    public bool WebDavEnabled { get; set; }

    /// <summary>Whether WebDAV Basic authentication is refused over plain HTTP.</summary>
    public bool WebDavRequireHttps { get; set; } = true;

    /// <summary>
    /// Reported WebDAV status. The service runs in-process (no reconciler), so this
    /// is written directly to reflect the desired state when the toggle is saved.
    /// </summary>
    public string WebDavStatus { get; private set; } = "Unbekannt";

    /// <summary>The connect URL shown as a read-only hint (e.g. <c>https://host:8443/dav/</c>).</summary>
    public static string BuildWebDavUrl(string baseUri) => $"{baseUri.TrimEnd('/')}/dav/";

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

            await _config.SetAsync(WebDavOptions.EnabledKey, WebDavEnabled);
            await _config.SetAsync(WebDavOptions.RequireHttpsKey, WebDavRequireHttps);
            // In-process service: no reconciler writes the status, so reflect the
            // desired state directly (mirrors the SMB status the host reports back).
            WebDavStatus = (WebDavEnabled ? DataServiceStatus.Running : DataServiceStatus.Stopped).ToString();
            await _config.SetAsync(WebDavOptions.StatusKey, WebDavStatus);

            _logger.LogInformation(
                "SMB service desired state set to {SmbEnabled}; WebDAV set to {WebDavEnabled}",
                SmbEnabled, WebDavEnabled);
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

    /// <summary>Last Host heartbeat (UTC), or <c>null</c> if the Host never reported one.</summary>
    public DateTime? HostLastSeenUtc { get; private set; }

    /// <summary>
    /// The Host process (which runs the SMB reconciler) has not reported recently, so
    /// <see cref="SmbStatus"/> is only the last value it wrote and may be outdated.
    /// </summary>
    public bool HostUnreachable { get; private set; }

    /// <summary>
    /// Re-reads the reported status of the SMB service from the config store. Never throws:
    /// it runs from UI event handlers and the post-save poll, where an unhandled database
    /// error would terminate the Blazor circuit. Returns false (and sets
    /// <see cref="ErrorMessage"/>) when the store could not be read.
    /// </summary>
    public async Task<bool> RefreshDataServiceStatusAsync()
    {
        if (!CanManageDataServices) return true;
        try
        {
            SmbStatus = await _config.GetFreshAsync(DataServiceKeys.StatusKey("smb"), "Unbekannt");
            WebDavStatus = await _config.GetFreshAsync(WebDavOptions.StatusKey, "Unbekannt");
            HostLastSeenUtc = await _config.GetFreshAsync<DateTime?>(DataServiceKeys.HostHeartbeatKey, null);
            HostUnreachable = HostLastSeenUtc is not { } lastSeen
                || DateTime.UtcNow - lastSeen.ToUniversalTime() > DataServiceKeys.HostHeartbeatStaleAfter;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the data service status failed");
            ErrorMessage = Resources.Web_Settings_Data_StatusRefreshFailed;
            return false;
        }
    }

    /// <summary>
    /// The SMB status the Host should report once it has applied <see cref="SmbEnabled"/>.
    /// The settings page polls until it appears instead of only promising it.
    /// </summary>
    public bool SmbStatusMatchesDesired =>
        SmbStatus == (SmbEnabled ? DataServiceStatus.Running : DataServiceStatus.Stopped).ToString();

    /// <summary>
    /// Persists the SMB protocol/security options (dialect range, signing,
    /// encryption). Samba's config sync pulls them within seconds and reloads its
    /// configuration; only new connections negotiate with the new settings.
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
            SuccessMessage = Resources.Web_Settings_SmbProtocolSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save SMB protocol settings");
            ErrorMessage = Resources.Web_Settings_DataServiceSaveFailed;
            return false;
        }
    }
}
