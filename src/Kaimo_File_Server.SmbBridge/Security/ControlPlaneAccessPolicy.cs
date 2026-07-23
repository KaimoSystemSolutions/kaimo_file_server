namespace Kaimo_File_Server.SmbBridge.Security;

/// <summary>
/// Least-privilege mapping from mutually authenticated Samba client
/// identities to the exact gRPC methods they may invoke.
/// </summary>
public static class ControlPlaneAccessPolicy
{
    public const string AuthSyncClient = "kaimo-auth-sync";
    public const string ShareSyncClient = "kaimo-share-sync";
    public const string ConfigSyncClient = "kaimo-config-sync";
    public const string RuntimeClient = "kaimo-samba-runtime";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedMethods =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [AuthSyncClient] = new HashSet<string>(StringComparer.Ordinal)
            {
                "/kaimo.smb.bridge.v1.AuthService/ListUsers",
            },
            [ShareSyncClient] = new HashSet<string>(StringComparer.Ordinal)
            {
                "/kaimo.smb.bridge.v1.ShareService/ListShares",
            },
            [ConfigSyncClient] = new HashSet<string>(StringComparer.Ordinal)
            {
                "/kaimo.smb.bridge.v1.ConfigService/GetProtocolSettings",
            },
            [RuntimeClient] = new HashSet<string>(StringComparer.Ordinal)
            {
                "/kaimo.smb.bridge.v1.AuthzService/AuthorizeConnect",
                "/kaimo.smb.bridge.v1.AuthzService/AuthorizeOpen",
                "/kaimo.smb.bridge.v1.AuthzService/AuthorizeDelete",
                "/kaimo.smb.bridge.v1.AuthzService/AuthorizeRename",
                "/kaimo.smb.bridge.v1.EventService/NotifyClose",
                "/kaimo.smb.bridge.v1.EventService/NotifyMkdir",
                "/kaimo.smb.bridge.v1.EventService/NotifyDelete",
                "/kaimo.smb.bridge.v1.EventService/NotifyRename",
                "/kaimo.smb.bridge.v1.SnapshotService/EnumerateSnapshots",
                "/kaimo.smb.bridge.v1.SnapshotService/ResolveVersion",
            },
        };

    public static bool IsKnownClient(string clientId) =>
        AllowedMethods.ContainsKey(clientId);

    public static bool IsAllowed(string clientId, string method) =>
        AllowedMethods.TryGetValue(clientId, out var methods) && methods.Contains(method);
}
