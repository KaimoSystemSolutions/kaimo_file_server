namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// SMB protocol dialect versions the server can negotiate. Mirrors the SMB
/// library's dialect enum but is declared here in Core so the config model
/// carries <b>no</b> compile-time dependency on the SMB transport assembly (only
/// <c>Kaimo_File_Server.Smb</c> references the library). The SMB host maps these
/// onto the library's <c>SmbDialect</c> when it builds the server.
///
/// The members are declared in ascending order so an ordinal comparison
/// (<c>(int)a &lt;= (int)b</c>) reflects "older … newer". SMB 1 is intentionally
/// absent — it is permanently disabled for security (EternalBlue/WannaCry family,
/// no signing/encryption) and can never be offered.
/// </summary>
public enum SmbProtocolVersion
{
    /// <summary>SMB 2.0.2 (Windows Vista / Server 2008).</summary>
    Smb202,

    /// <summary>SMB 2.1 (Windows 7 / Server 2008 R2).</summary>
    Smb210,

    /// <summary>SMB 3.0 (Windows 8 / Server 2012) — first with encryption.</summary>
    Smb300,

    /// <summary>SMB 3.0.2 (Windows 8.1 / Server 2012 R2).</summary>
    Smb302,

    /// <summary>SMB 3.1.1 (Windows 10 / Server 2016) — AES-GCM, pre-auth integrity.</summary>
    Smb311,
}

/// <summary>
/// Runtime-configurable SMB protocol/security options, edited on the settings
/// page ("Datendienste" tab) and persisted as a JSON object under
/// <see cref="ConfigKey"/> via <c>IConfigRepository</c>. Read fresh by the SMB
/// host (see <see cref="ISmbConfigStore"/>) when the server (re)starts, since the
/// two run in separate processes.
///
/// The defaults reproduce the SMB library's own secure defaults (dialect range
/// 2.0.2 … 3.1.1, signing required, encryption optional), so an unconfigured
/// system behaves exactly as before this setting existed.
/// </summary>
public sealed class SmbProtocolSettings
{
    public const string ConfigKey = "services.smb.protocol";

    /// <summary>Lowest dialect the server will negotiate (the "minimum version").</summary>
    public SmbProtocolVersion MinVersion { get; set; } = SmbProtocolVersion.Smb202;

    /// <summary>Highest dialect the server will negotiate.</summary>
    public SmbProtocolVersion MaxVersion { get; set; } = SmbProtocolVersion.Smb311;

    /// <summary>
    /// Require SMB message signing (tamper protection). Default <c>true</c>;
    /// disabling it allows unsigned sessions and is discouraged.
    /// </summary>
    public bool RequireSigning { get; set; } = true;

    /// <summary>
    /// Require SMB3 encryption globally. Default <c>false</c> — encryption is
    /// only available from SMB 3.0 upward, so enabling it effectively excludes
    /// SMB 2.x clients.
    /// </summary>
    public bool RequireEncryption { get; set; } = false;

    /// <summary>
    /// Announce the server via WS-Discovery so it appears in Windows Explorer's
    /// "Network" view (a UDP responder on port 3702). Default <c>true</c>. Has no
    /// effect when the server cannot reach the LAN broadcast domain (e.g. a
    /// bridged container without host networking), where clients must still map
    /// the share by its <c>\\host\share</c> path.
    /// </summary>
    public bool EnableWsDiscovery { get; set; } = true;

    /// <summary>
    /// Emit structured SMB security-audit events (authentication, share access,
    /// file open/close/delete, permission changes, session/connection lifecycle)
    /// to the server log. Default <c>true</c>.
    /// </summary>
    public bool EnableAuditLog { get; set; } = true;

    public static SmbProtocolSettings Default() => new();

    /// <summary>
    /// Repairs a nonsensical range in place: if <see cref="MinVersion"/> is newer
    /// than <see cref="MaxVersion"/> the maximum is raised to match the minimum,
    /// so the server always has at least one negotiable dialect.
    /// </summary>
    public void Normalize()
    {
        if (MinVersion > MaxVersion)
            MaxVersion = MinVersion;
    }
}

/// <summary>
/// Config keys for the SMB-specific protocol options. Written by the Web UI and
/// read by the SMB host process, following the <c>services.smb.*</c> convention
/// of <see cref="DataServiceKeys"/>.
/// </summary>
public static class SmbConfigKeys
{
    /// <summary>JSON object: the full <see cref="SmbProtocolSettings"/>.</summary>
    public const string ProtocolKey = SmbProtocolSettings.ConfigKey;

    /// <summary>
    /// Stable per-installation server GUID (string). Persisted once and reused so
    /// clients recognise the same SMB server across restarts (NEGOTIATE identity,
    /// durable-handle reconnect, WS-Discovery endpoint id). A missing/blank value
    /// is generated on first start.
    /// </summary>
    public const string ServerGuidKey = "services.smb.serverGuid";
}

/// <summary>
/// Cross-process-safe reader for the SMB protocol configuration. The interface
/// lives in Core so the SMB transport (which only references Core, not
/// Infrastructure) can consume it; the implementation lives in Infrastructure
/// over <c>IConfigRepository</c>. Mirrors the <c>ISearchConfigStore</c> pattern.
/// </summary>
public interface ISmbConfigStore
{
    /// <summary>
    /// Reads the desired SMB protocol settings fresh (bypassing the cache),
    /// because they are written in the Web process but read in the SMB host
    /// process — a cached value would hide the change for up to the cache TTL.
    /// </summary>
    Task<SmbProtocolSettings> GetProtocolSettingsAsync();

    /// <summary>
    /// Returns the stable server GUID, generating and persisting one on first use.
    /// Idempotent: subsequent calls return the same value so the SMB server keeps
    /// its identity across restarts.
    /// </summary>
    Task<Guid> GetOrCreateServerGuidAsync();
}
