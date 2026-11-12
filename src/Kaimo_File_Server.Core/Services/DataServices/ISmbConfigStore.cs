namespace Kaimo_File_Server.Core.Services.DataServices;

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

    /// <summary>
    /// Whether the SMB service is enabled (the "Datendienste" on/off toggle,
    /// <c>services.smb.enabled</c>). Read fresh, since the flag is written by the
    /// Web process. Since the Phase 5 cutover, smbd runs in a separate container:
    /// this flag is enforced by the bridge's <c>AuthorizeConnect</c> (deny-all when
    /// disabled), which is what actually makes a disabled service reject every
    /// TREE_CONNECT. Defaults to <c>true</c> so an unconfigured system stays on.
    /// </summary>
    Task<bool> IsSmbEnabledAsync();
}
