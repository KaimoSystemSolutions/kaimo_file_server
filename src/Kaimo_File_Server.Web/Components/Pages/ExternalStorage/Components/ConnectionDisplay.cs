using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Components.Shared;

namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

/// <summary>
/// Display helpers for storage connections (provider icon/color, state, authorization and
/// error-code wording), shared by the connection and sync administration tabs.
/// </summary>
internal static class ConnectionDisplay
{
    public static string ProviderIcon(string id) => id.ToLowerInvariant() switch
    {
        "smb" => "connection_smb.svg",
        "rsync-ssh" => "connection_rsync.svg",
        "sftp" => "connection_sftp.svg",
        "webdav" => "connection_webdav.svg",
        "onedrive" => "connection_onedrive.svg",
        "google" or "googledrive" => "connection_google.svg",
        "dropbox" => "connection_dropbox.svg",
        _ => "cloud-download.svg"
    };
    public static string ProviderColor(string id) => id.ToLowerInvariant() switch
    {
        "smb" => "var(--provider-smb)",
        "rsync-ssh" => "var(--provider-rsync)",
        "sftp" => "var(--provider-sftp)",
        "webdav" => "var(--provider-webdav)",
        "onedrive" => "var(--provider-onedrive)",
        "google" or "googledrive" => "var(--provider-google)",
        "dropbox" => "var(--provider-dropbox)",
        _ => "var(--text-secondary)"
    };
    public static string StateName(StorageConnectionState state) => R($"Web_ExternalStorage_State_{state}");
    public static StatusTone StateTone(StorageConnectionState state) => state switch { StorageConnectionState.Ready => StatusTone.Positive, StorageConnectionState.Disabled => StatusTone.Neutral, _ => StatusTone.Warning };
    public static string AuthorizationName(StorageAuthorizationMode mode) => R($"Web_ExternalStorage_Auth_{mode}");
    public static string SecretHealth(StorageConnection connection) => connection.AuthorizationMode switch
    {
        StorageAuthorizationMode.NetworkIdentity => R("Web_ExternalStorage_RuntimeIdentity"),
        StorageAuthorizationMode.SshKey => string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
            ? R("Web_ExternalStorage_SecretFileReferences")
            : R("Web_ExternalStorage_Protected"),
        _ => string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
            ? R("Web_ExternalStorage_NotAuthorized")
            : R("Web_ExternalStorage_Protected")
    };

    // Explicit, localized message for a persisted connection error code, with a
    // generic "attention required (code)" fallback for unmapped provider codes.
    public static string ErrorMessage(string code)
        => Resources.ResourceManager.GetString($"Web_ExternalStorage_ErrorCode_{code}")
           ?? string.Format(
               Resources.ResourceManager.GetString("Web_ExternalStorage_ErrorCode_Unknown")
               ?? "Attention required ({0})", code);

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
