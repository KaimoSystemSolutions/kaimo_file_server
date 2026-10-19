using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

public partial class AuthzGrpcService
{
    /// <summary>
    /// Complete SMB desired-access authorization. Generic access is expanded
    /// exactly as Samba 4.19.5 does. Every specific security-relevant bit is
    /// checked independently, and MAXIMUM_ALLOWED is converted into an
    /// attenuated specific mask that the VFS passes to Samba.
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeOpen(
        AuthorizeOpenRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidContext(request.Username, request.Share))
            return Deny("invalid user/share context");
        CancellationToken cancellationToken =
            context?.CancellationToken ?? CancellationToken.None;

        var user = await _auth.ResolveUserContextAsync(request.Username)
            .WaitAsync(cancellationToken);
        if (user is null)
            return Deny($"unknown user '{request.Username}'");

        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, cancellationToken);
        if (share is null)
            return Deny($"unknown or disabled share '{request.Share}'");

        if (!TryResolveClientPath(
                share, request.Path, allowRoot: true,
                out string normalized, out string full))
            return Deny($"invalid open path '{request.Path}'");

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
        bool reservedUserTarget =
            ShareEntryPolicy.IsReservedForUserWrites(normalized, share.RecycleRootDepth);

        if (reservedUserTarget &&
            (!exists && request.WantsCreate ||
             (specific & UserContentMutationAccess) != 0))
            return Deny(
                $"open denied: '{normalized}' is a reserved Kaimo namespace");

        // SMB clients keep a handle on the name they created, so a new entry Windows could not
        // use is refused (as a Windows server would) instead of being renamed later.
        if (!exists && request.WantsCreate && !IsWindowsValidLeaf(normalized))
            return Deny($"create denied: '{normalized}' is not valid on Windows");

        string? traversalDeny = await CheckTraversalAsync(
            user, share.Id, normalized, cancellationToken);
        if (traversalDeny is not null)
            return Deny(traversalDeny);

        if (!exists && request.WantsCreate)
        {
            string parent = ShareRelativePath.GetParent(normalized);
            FilePermission createPermission = request.CreateDirectory
                ? FilePermission.CreateAppendData
                : FilePermission.CreateWriteData;
            if (!await _acl.HasAccessAsync(
                    user, share.Id, parent, true, createPermission)
                .WaitAsync(cancellationToken))
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
            ? await CalculateMaximumAllowedAsync(
                user, share.Id, normalized, isDir, cancellationToken)
            : await RequireSpecificAccessAsync(
                user, share.Id, normalized, isDir, specific,
                cancellationToken);

        bool allow = decision.Allowed;
        uint granted = allow ? decision.GrantedMask : 0;
        string reason = decision.Reason;

        if (allow)
        {
            // Preserve a requested SYNCHRONIZE bit. MAXIMUM_ALLOWED receives it
            // as well because SMB clients may set/ignore it freely.
            if ((specific & Synchronize) != 0 || wantsMaximum)
                granted |= Synchronize;
            if (reservedUserTarget && wantsMaximum)
                granted &= ~UserContentMutationAccess;
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
        UserContext user, Guid shareId, string normalized,
        CancellationToken cancellationToken)
    {
        var hierarchy = ShareRelativePath.BuildHierarchy(normalized);
        // The last component is the target. Every preceding component is a
        // directory that Samba traverses, including the share root.
        for (int i = 0; i < hierarchy.Count - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ancestor = hierarchy[i];
            if (!await _acl.HasAccessAsync(
                    user, shareId, ancestor, true,
                    FilePermission.TraverseExecute)
                .WaitAsync(cancellationToken))
                return $"traverse denied: ancestor '{ancestor}' lacks TraverseExecute";
        }
        return null;
    }

    private async Task<AccessDecision> RequireSpecificAccessAsync(
        UserContext user, Guid shareId, string normalized,
        bool isDirectory, uint specific,
        CancellationToken cancellationToken)
    {
        foreach (AccessRule rule in AccessRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    user, shareId, normalized, isDirectory,
                    cancellationToken);
                allowed = deleteDecision.Allowed;
                reason = deleteDecision.Reason;
            }
            else
            {
                allowed = await _acl.HasAccessAsync(
                    user, shareId, normalized, isDirectory,
                    rule.Permission).WaitAsync(cancellationToken);
                reason = $"access denied: {rule.Name}";
            }

            if (!allowed)
                return new(false, 0, reason);
        }

        return new(true, specific, "");
    }

    private async Task<AccessDecision> CalculateMaximumAllowedAsync(
        UserContext user, Guid shareId, string normalized, bool isDirectory,
        CancellationToken cancellationToken)
    {
        uint granted = 0;
        foreach (AccessRule rule in AccessRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule.Mask == FileDeleteChild && !isDirectory)
                continue;

            bool allowed;
            if (rule.Mask == DeleteAccess)
            {
                var deleteDecision = await CanDeleteAsync(
                    user, shareId, normalized, isDirectory,
                    cancellationToken);
                allowed = deleteDecision.Allowed;
            }
            else
            {
                allowed = await _acl.HasAccessAsync(
                    user, shareId, normalized, isDirectory,
                    rule.Permission).WaitAsync(cancellationToken);
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
}
