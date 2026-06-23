namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Which SMB protocol dialects the server offers during negotiation. SMB1 is
/// never offered (permanently disabled for security — EternalBlue/WannaCry
/// family, no signing/encryption) and is therefore not represented here.
/// At least one dialect must be enabled for the service to be reachable.
/// </summary>
public sealed record SmbDialectConfig(bool EnableSmb2, bool EnableSmb3)
{
    /// <summary>Backward-compatible default: both modern dialects enabled.</summary>
    public static SmbDialectConfig Default => new(true, true);
}

/// <summary>
/// Config keys for the SMB-specific protocol options. Written by the Web UI and
/// read by the SMB host process, following the <c>services.smb.*</c> convention
/// of <see cref="DataServiceKeys"/>.
/// </summary>
public static class SmbConfigKeys
{
    /// <summary>Bool flag: offer SMB 2.x during dialect negotiation.</summary>
    public const string Smb2EnabledKey = "services.smb.smb2.enabled";

    /// <summary>Bool flag: offer SMB 3.x during dialect negotiation.</summary>
    public const string Smb3EnabledKey = "services.smb.smb3.enabled";
}

/// <summary>
/// Cross-process-safe reader for the SMB dialect configuration. The interface
/// lives in Core so the SMB transport (which only references Core, not
/// Infrastructure) can consume it; the implementation lives in Infrastructure
/// over <c>IConfigRepository</c>. Mirrors the <c>ISearchConfigStore</c> pattern.
/// </summary>
public interface ISmbConfigStore
{
    /// <summary>
    /// Reads the desired SMB dialects fresh (bypassing the cache), because the
    /// flags are written in the Web process but read in the SMB host process.
    /// </summary>
    Task<SmbDialectConfig> GetDialectConfigAsync();
}
