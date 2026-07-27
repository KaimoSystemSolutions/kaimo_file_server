namespace Kaimo_File_Server.SmbBridge.Security;

/// <summary>
/// Allow-list for the mutually authenticated Samba workload identity.
/// GetNtHash deliberately remains unassigned.
/// </summary>
public static class ControlPlaneAccessPolicy
{
    public const string SambaClient = "kaimo-samba";

    private static readonly IReadOnlySet<string> AllowedMethods =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "/kaimo.smb.bridge.v1.AuthService/ListUsers",
            "/kaimo.smb.bridge.v1.ShareService/ListShares",
            "/kaimo.smb.bridge.v1.ConfigService/GetProtocolSettings",
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
        };

    public static bool IsKnownClient(string clientId) =>
        string.Equals(clientId, SambaClient, StringComparison.Ordinal);

    public static bool IsAllowed(string clientId, string method) =>
        IsKnownClient(clientId) && AllowedMethods.Contains(method);
}
