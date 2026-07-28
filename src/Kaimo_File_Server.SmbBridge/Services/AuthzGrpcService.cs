using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
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
public sealed class AuthzGrpcService : AuthzService.AuthzServiceBase
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

    public AuthzGrpcService(
        IUserRepository users,
        IShareRepository shares,
        IAuthenticationLookup auth,
        IAclService acl,
        ISmbConfigStore config,
        ILogger<AuthzGrpcService> logger)
    {
        _users = users;
        _shares = shares;
        _auth = auth;
        _acl = acl;
        _config = config;
        _logger = logger;
    }

    public override async Task<AuthorizeReply> AuthorizeConnect(
        AuthorizeConnectRequest request, ServerCallContext context)
    {
        // Phase 5 cutover: the SMB on/off toggle. The old in-process SmbServer was
        // start/stopped by the host reconciler; smbd now runs in its own container.
        // Enforcing the flag here (deny every TREE_CONNECT when disabled) is what
        // makes "SMB off" actually block clients — no share becomes enterable.
        if (!await _config.IsSmbEnabledAsync())
            return Deny($"smb service disabled (services.smb.enabled=false)");

        var user = await _users.GetByUsernameAsync(request.Username);
        if (user is null || !user.IsEnabled)
            return Deny($"unknown or disabled user '{request.Username}'");

        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, context?.CancellationToken ?? CancellationToken.None);
        if (share is null)
            return Deny($"unknown or disabled share '{request.Share}'");

        bool allow = await _auth.CanAccessShareAsync(share.Id, user.Id);

        _logger.LogInformation(
            "AuthorizeConnect: user={User} share={Share} -> {Decision}",
            request.Username, request.Share, allow ? "ALLOW" : "DENY");

        return new AuthorizeReply { Allow = allow, Reason = allow ? "" : "acl denied" };
    }

    /// <summary>
    /// Complete SMB desired-access authorization. Generic access is expanded
    /// exactly as Samba 4.19.5 does. Every specific security-relevant bit is
    /// checked independently, and MAXIMUM_ALLOWED is converted into an
    /// attenuated specific mask that the VFS passes to Samba.
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeOpen(
        AuthorizeOpenRequest request, ServerCallContext context)
    {
        var user = await _auth.ResolveUserContextAsync(request.Username);
        if (user is null)
            return Deny($"unknown user '{request.Username}'");

        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, context?.CancellationToken ?? CancellationToken.None);
        if (share is null)
            return Deny($"unknown or disabled share '{request.Share}'");

        if (!ShareRelativePath.IsValid(request.Path))
            return Deny($"invalid open path '{request.Path}'");

        string normalized = ShareRelativePath.Normalize(request.Path);
        string full = Path.Combine(share.Path, normalized);
        bool exists = File.Exists(full) || Directory.Exists(full);
        bool isDir = exists ? Directory.Exists(full) : request.CreateDirectory;

        uint expanded = ExpandGenericAccess(request.AccessMask);
        uint unsupported = expanded &
            ~(SupportedSpecificAccess | MaximumAllowed | SystemSecurity);
        if (unsupported != 0)
            return Deny($"unsupported access-mask bits 0x{unsupported:X8}");

        if ((expanded & SystemSecurity) != 0)
            return Deny("ACCESS_SYSTEM_SECURITY is unsupported by the Kaimo ACL model");

        bool wantsMaximum = (expanded & MaximumAllowed) != 0;
        uint specific = expanded & ~MaximumAllowed;

        string? traversalDeny = await CheckTraversalAsync(
            user, share.Id, normalized);
        if (traversalDeny is not null)
            return Deny(traversalDeny);

        if (!exists && request.WantsCreate)
        {
            string parent = ShareRelativePath.GetParent(normalized);
            FilePermission createPermission = request.CreateDirectory
                ? FilePermission.CreateAppendData
                : FilePermission.CreateWriteData;
            if (!await _acl.HasAccessAsync(
                    user, share.Id, parent, true, createPermission))
                return Deny(
                    $"create denied: parent '{parent}' lacks {createPermission}");
        }

        // Samba 4.19.5 unconditionally adds FILE_READ_ATTRIBUTES to a
        // successfully opened FSP. Require it here even if the client omitted
        // the bit, otherwise the broad POSIX identity would over-grant Kaimo.
        // readdir visibility checks do not create an FSP and are exempt.
        if (!request.DirectoryListing)
            specific |= FileReadAttributes;

        AccessDecision decision = wantsMaximum
            ? await CalculateMaximumAllowedAsync(user, share.Id, normalized, isDir)
            : await RequireSpecificAccessAsync(
                user, share.Id, normalized, isDir, specific);

        bool allow = decision.Allowed;
        uint granted = allow ? decision.GrantedMask : 0;
        string reason = decision.Reason;

        if (allow)
        {
            // Preserve a requested SYNCHRONIZE bit. MAXIMUM_ALLOWED receives it
            // as well because SMB clients may set/ignore it freely.
            if ((specific & Synchronize) != 0 || wantsMaximum)
                granted |= Synchronize;
        }

        if (allow)
            _logger.LogDebug(
                "AuthorizeOpen ALLOW: user={User} share={Share} path=[{Path}] requested=0x{Requested:X8} granted=0x{Granted:X8} create={Create} dir={Dir} listing={Listing}",
                request.Username, request.Share, request.Path,
                request.AccessMask, granted, request.WantsCreate,
                request.CreateDirectory, request.DirectoryListing);
        else
            _logger.LogInformation(
                "AuthorizeOpen DENY: user={User} share={Share} path=[{Path}] requested=0x{Requested:X8} create={Create} dir={Dir} listing={Listing} — {Reason}",
                request.Username, request.Share, request.Path,
                request.AccessMask, request.WantsCreate,
                request.CreateDirectory, request.DirectoryListing, reason);

        return new AuthorizeReply
        {
            Allow = allow,
            Reason = allow ? "" : reason,
            GrantedAccessMask = granted
        };
    }

    private readonly record struct AccessDecision(
        bool Allowed, uint GrantedMask, string Reason);

    private static uint ExpandGenericAccess(uint accessMask)
    {
        uint expanded = accessMask & ~GenericBits;
        if ((accessMask & GenericAll) != 0)
            expanded |= FileGenericAll;
        if ((accessMask & GenericRead) != 0)
            expanded |= FileGenericRead;
        if ((accessMask & GenericWrite) != 0)
            expanded |= FileGenericWrite;
        if ((accessMask & GenericExecute) != 0)
            expanded |= FileGenericExecute;
        return expanded;
    }

    private async Task<string?> CheckTraversalAsync(
        UserContext user, Guid shareId, string normalized)
    {
        var hierarchy = ShareRelativePath.BuildHierarchy(normalized);
        // The last component is the target. Every preceding component is a
        // directory that Samba traverses, including the share root.
        for (int i = 0; i < hierarchy.Count - 1; i++)
        {
            string ancestor = hierarchy[i];
            if (!await _acl.HasAccessAsync(
                    user, shareId, ancestor, true,
                    FilePermission.TraverseExecute))
                return $"traverse denied: ancestor '{ancestor}' lacks TraverseExecute";
        }
        return null;
    }

    private async Task<AccessDecision> RequireSpecificAccessAsync(
        UserContext user, Guid shareId, string normalized,
        bool isDirectory, uint specific)
    {
        foreach (AccessRule rule in AccessRules)
        {
            if ((specific & rule.Mask) == 0)
                continue;

            if (rule.Mask == FileDeleteChild && !isDirectory)
                return new(false, 0,
                    "FILE_DELETE_CHILD is valid only for directories");

            bool allowed;
            string reason;
            if (rule.Mask == DeleteAccess)
            {
                var deleteDecision = await CanDeleteAsync(
                    user, shareId, normalized, isDirectory);
                allowed = deleteDecision.Allowed;
                reason = deleteDecision.Reason;
            }
            else
            {
                allowed = await _acl.HasAccessAsync(
                    user, shareId, normalized, isDirectory,
                    rule.Permission);
                reason = $"access denied: {rule.Name}";
            }

            if (!allowed)
                return new(false, 0, reason);
        }

        return new(true, specific, "");
    }

    private async Task<AccessDecision> CalculateMaximumAllowedAsync(
        UserContext user, Guid shareId, string normalized, bool isDirectory)
    {
        uint granted = 0;
        foreach (AccessRule rule in AccessRules)
        {
            if (rule.Mask == FileDeleteChild && !isDirectory)
                continue;

            bool allowed;
            if (rule.Mask == DeleteAccess)
            {
                var deleteDecision = await CanDeleteAsync(
                    user, shareId, normalized, isDirectory);
                allowed = deleteDecision.Allowed;
            }
            else
            {
                allowed = await _acl.HasAccessAsync(
                    user, shareId, normalized, isDirectory,
                    rule.Permission);
            }

            if (allowed)
                granted |= rule.Mask;
        }

        // Samba will add this right to the FSP even if the client omitted it.
        if ((granted & FileReadAttributes) == 0)
            return new(false, 0,
                "maximum access denied: Samba would grant FILE_READ_ATTRIBUTES but Kaimo denies ReadAttributes");

        return new(true, granted, "");
    }

    /// <summary>
    /// Authorizes the destructive unlink/rmdir operation before Samba touches the
    /// filesystem. Windows permits deletion through either Delete on the target or
    /// DeleteSubItems on the containing directory; Kaimo mirrors those semantics.
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeDelete(
        AuthorizeDeleteRequest request, ServerCallContext context)
    {
        var user = await _auth.ResolveUserContextAsync(request.Username);
        if (user is null)
            return Deny($"unknown user '{request.Username}'");

        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, context?.CancellationToken ?? CancellationToken.None);
        if (share is null)
            return Deny($"unknown or disabled share '{request.Share}'");

        string normalized = ShareRelativePath.Normalize(request.Path);
        if (string.IsNullOrEmpty(normalized) || !ShareRelativePath.IsValid(request.Path))
            return Deny($"invalid delete path '{request.Path}'");

        var decision = await CanDeleteAsync(
            user, share.Id, normalized, request.IsDirectory);
        bool allow = decision.Allowed;
        string reason = decision.Reason;

        if (allow)
            _logger.LogDebug(
                "AuthorizeDelete ALLOW: user={User} share={Share} path=[{Path}] dir={Dir} via={Source}",
                request.Username, request.Share, normalized, request.IsDirectory,
                decision.Source);
        else
            _logger.LogInformation(
                "AuthorizeDelete DENY: user={User} share={Share} path=[{Path}] dir={Dir} — {Reason}",
                request.Username, request.Share, normalized, request.IsDirectory, reason);

        return new AuthorizeReply { Allow = allow, Reason = reason };
    }

    /// <summary>
    /// Authorizes a rename as one indivisible policy decision: remove the
    /// source, create the source object type in the destination parent, and,
    /// when applicable, remove the replacement target. The native VFS hook
    /// revalidates the filesystem identities after this RPC and before renameat.
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeRename(
        AuthorizeRenameRequest request, ServerCallContext context)
    {
        var user = await _auth.ResolveUserContextAsync(request.Username);
        if (user is null)
            return Deny($"unknown user '{request.Username}'");

        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, context?.CancellationToken ?? CancellationToken.None);
        if (share is null)
            return Deny($"unknown or disabled share '{request.Share}'");

        if (!ShareRelativePath.IsValid(request.SourcePath) ||
            !ShareRelativePath.IsValid(request.DestinationPath))
            return Deny("invalid rename path");

        string source = ShareRelativePath.Normalize(request.SourcePath);
        string destination = ShareRelativePath.Normalize(request.DestinationPath);
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination) ||
            string.Equals(source, destination, StringComparison.Ordinal))
            return Deny($"invalid rename '{source}' -> '{destination}'");

        // Cross-check the native request against the shared storage view. This
        // is not the final TOCTOU guard (the VFS performs that immediately
        // before renameat), but prevents authorization based on stale/type-
        // confused request metadata.
        string sourceFull = Path.Combine(share.Path, source);
        bool sourceExists = File.Exists(sourceFull) || Directory.Exists(sourceFull);
        bool sourceIsDirectory = sourceExists && Directory.Exists(sourceFull);
        if (!sourceExists || sourceIsDirectory != request.SourceIsDirectory)
            return Deny("rename source no longer matches the native request");

        string destinationFull = Path.Combine(share.Path, destination);
        bool destinationExists = File.Exists(destinationFull) || Directory.Exists(destinationFull);
        bool destinationIsDirectory = destinationExists && Directory.Exists(destinationFull);
        if (destinationExists != request.DestinationExists ||
            (destinationExists &&
             destinationIsDirectory != request.DestinationIsDirectory))
            return Deny("rename destination no longer matches the native request");

        if (request.DestinationExists != request.ReplaceIntent)
            return Deny("rename replacement intent does not match destination state");
        if (!request.DestinationExists && request.DestinationIsDirectory)
            return Deny("missing rename destination cannot have an object type");

        string? sourceTraversalDeny = await CheckTraversalAsync(
            user, share.Id, source);
        if (sourceTraversalDeny is not null)
            return Deny(sourceTraversalDeny);

        string? destinationTraversalDeny = await CheckTraversalAsync(
            user, share.Id, destination);
        if (destinationTraversalDeny is not null)
            return Deny(destinationTraversalDeny);

        var sourceDelete = await CanDeleteAsync(
            user, share.Id, source, request.SourceIsDirectory);
        if (!sourceDelete.Allowed)
            return Deny(sourceDelete.Reason);

        string destinationParent = ShareRelativePath.GetParent(destination);
        FilePermission createPermission = request.SourceIsDirectory
            ? FilePermission.CreateAppendData
            : FilePermission.CreateWriteData;
        if (!await _acl.HasAccessAsync(
                user, share.Id, destinationParent, true, createPermission))
            return Deny(
                $"rename denied: destination parent '{destinationParent}' lacks {createPermission}");

        string replacementSource = "none";
        if (request.DestinationExists)
        {
            var replacementDelete = await CanDeleteAsync(
                user, share.Id, destination,
                request.DestinationIsDirectory);
            if (!replacementDelete.Allowed)
                return Deny(replacementDelete.Reason);
            replacementSource = replacementDelete.Source;
        }

        _logger.LogDebug(
            "AuthorizeRename ALLOW: user={User} share={Share} source=[{Source}] destination=[{Destination}] dir={Directory} replace={Replace} sourceDelete={SourceDelete} replacementDelete={ReplacementDelete}",
            request.Username, request.Share, source, destination,
            request.SourceIsDirectory, request.DestinationExists,
            sourceDelete.Source, replacementSource);

        return new AuthorizeReply { Allow = true };
    }

    private async Task<(bool Allowed, string Source, string Reason)> CanDeleteAsync(
        UserContext user, Guid shareId, string normalized, bool isDirectory)
    {
        if (await _acl.HasAccessAsync(
                user, shareId, normalized, isDirectory, FilePermission.Delete))
            return (true, "Delete", "");

        string parent = ShareRelativePath.GetParent(normalized);
        if (await _acl.HasAccessAsync(
                user, shareId, parent, true, FilePermission.DeleteSubItems))
            return (true, "DeleteSubItems", "");

        return (false, "none",
            $"delete denied: target '{normalized}' lacks Delete and parent '{parent}' lacks DeleteSubItems");
    }

    private AuthorizeReply Deny(string reason)
    {
        _logger.LogInformation("Authz DENY: {Reason}", reason);
        return new AuthorizeReply { Allow = false, Reason = reason };
    }
}
