using Grpc.Core;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for the authorization control plane (Phase 2). Decides based on
/// real Kaimo ACLs whether a user can enter a share — same semantics as the
/// earlier <c>KaimoSharePolicy.AuthorizeConnect</c>
/// (→ <see cref="IAuthenticationLookup.CanAccessShareAsync"/>).
///
/// The Samba sidecar <c>kaimo_authd</c> calls this in the VFS connect hook and
/// translates names (user/share) — GUID resolution happens here.
/// </summary>
public sealed partial class AuthzGrpcService : AuthzService.AuthzServiceBase
{
    // MS-SMB2 2.2.13.1 and Samba 4.19.5 libcli/security/security.h.
    // Directory-specific aliases use the same numeric bits as their file
    // counterparts (LIST/READ_DATA, ADD_FILE/WRITE_DATA, etc.).
    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileAppendData = 0x00000004;
    private const uint FileReadEa = 0x00000008;
    private const uint FileWriteEa = 0x00000010;
    private const uint FileExecute = 0x00000020;
    private const uint FileDeleteChild = 0x00000040;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Synchronize = 0x00100000;
    private const uint SystemSecurity = 0x01000000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint GenericAll = 0x10000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericBits = GenericAll | GenericExecute | GenericWrite | GenericRead;

    // Exact Samba 4.19.5 file_generic_mapping outputs.
    private const uint FileGenericAll = 0x001F01FF;
    private const uint FileGenericRead = 0x00120089;
    private const uint FileGenericWrite = 0x00120116;
    private const uint FileGenericExecute = 0x001200A0;
    private const uint SupportedSpecificAccess = FileGenericAll;
    private const uint UserContentMutationAccess =
        FileWriteData | FileAppendData | FileWriteEa |
        FileWriteAttributes | WriteDac | WriteOwner;

    private static readonly AccessRule[] AccessRules =
    [
        new(FileReadData, FilePermission.ListReadData, "ListReadData"),
        new(FileWriteData, FilePermission.CreateWriteData, "CreateWriteData"),
        new(FileAppendData, FilePermission.CreateAppendData, "CreateAppendData"),
        new(FileReadEa, FilePermission.ReadExtAttributes, "ReadExtAttributes"),
        new(FileWriteEa, FilePermission.WriteExtAttributes, "WriteExtAttributes"),
        new(FileExecute, FilePermission.TraverseExecute, "TraverseExecute"),
        new(FileDeleteChild, FilePermission.DeleteSubItems, "DeleteSubItems"),
        new(FileReadAttributes, FilePermission.ReadAttributes, "ReadAttributes"),
        new(FileWriteAttributes, FilePermission.WriteAttributes, "WriteAttributes"),
        new(DeleteAccess, FilePermission.Delete, "Delete"),
        new(ReadControl, FilePermission.ReadPermissions, "ReadPermissions"),
        new(WriteDac, FilePermission.ChangePermissions, "ChangePermissions"),
        new(WriteOwner, FilePermission.TakeOwnership, "TakeOwnership")
    ];

    private readonly record struct AccessRule(
        uint Mask, FilePermission Permission, string Name);

    private readonly IUserRepository _users;
    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly IAclService _acl;
    private readonly ISmbConfigStore _config;
    private readonly ILogger<AuthzGrpcService> _logger;
    private readonly ICloudSyncOperationCoordinator? _cloudSyncOperations;

    public AuthzGrpcService(
        IUserRepository users,
        IShareRepository shares,
        IAuthenticationLookup auth,
        IAclService acl,
        ISmbConfigStore config,
        ILogger<AuthzGrpcService> logger,
        ICloudSyncOperationCoordinator? cloudSyncOperations = null)
    {
        _users = users;
        _shares = shares;
        _auth = auth;
        _acl = acl;
        _config = config;
        _logger = logger;
        _cloudSyncOperations = cloudSyncOperations;
    }

    public override async Task<AuthorizeReply> AuthorizeConnect(
        AuthorizeConnectRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidContext(request.Username, request.Share))
            return Deny("invalid user/share context");
        CancellationToken cancellationToken =
            context?.CancellationToken ?? CancellationToken.None;

        // Phase 5 cutover: the SMB on/off toggle. The old in-process SmbServer was
        // start/stopped by the host reconciler; smbd now runs in its own container.
        // Enforcing the flag here (deny every TREE_CONNECT when disabled) is what
        // makes "SMB off" actually block clients — no share becomes enterable.
        if (!await _config.IsSmbEnabledAsync().WaitAsync(cancellationToken))
            return Deny($"smb service disabled (services.smb.enabled=false)");

        var user = await _users.GetByUsernameAsync(request.Username)
            .WaitAsync(cancellationToken);
        if (user is null || !user.IsEnabled)
            return Deny($"unknown or disabled user '{request.Username}'");

        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, cancellationToken);
        if (share is null)
            return Deny($"unknown or disabled share '{request.Share}'");

        bool allow = await _auth.CanAccessShareAsync(share.Id, user.Id)
            .WaitAsync(cancellationToken);

        _logger.LogInformation(
            "AuthorizeConnect: user={User} share={Share} -> {Decision}",
            request.Username, request.Share, allow ? "ALLOW" : "DENY");

        return new AuthorizeReply { Allow = allow, Reason = allow ? "" : "acl denied" };
    }

    private AuthorizeReply Deny(string reason)
    {
        _logger.LogInformation("Authz DENY: {Reason}", reason);
        return new AuthorizeReply { Allow = false, Reason = reason };
    }

    private static bool TryResolveClientPath(
        ShareDefinition share,
        string rawPath,
        bool allowRoot,
        out string normalized,
        out string absolute)
    {
        absolute = "";
        if (!ShareRelativePath.TryNormalizeStrict(
                rawPath, out normalized, allowRoot,
                allowInternalNamespace: false,
                rootDepth: share.RecycleRootDepth))
            return false;

        try
        {
            absolute = ShareRelativePath.ToContainedAbsolutePath(
                share.Path, normalized, allowRoot,
                allowInternalNamespace: false);
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException or
                  NotSupportedException or UnauthorizedAccessException)
        {
            normalized = "";
            absolute = "";
            return false;
        }
    }
}
