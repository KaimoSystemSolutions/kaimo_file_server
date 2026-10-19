using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

public partial class AuthzGrpcService
{
    /// <summary>
    /// Authorizes the destructive unlink/rmdir operation before Samba touches the
    /// filesystem. Windows permits deletion through either Delete on the target or
    /// DeleteSubItems on the containing directory; Kaimo mirrors those semantics.
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeDelete(
        AuthorizeDeleteRequest request, ServerCallContext context)
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
                share, request.Path, allowRoot: false,
                out string normalized, out _))
            return Deny($"invalid delete path '{request.Path}'");

        var decision = await CanDeleteAsync(
            user, share.Id, normalized, request.IsDirectory,
            cancellationToken);
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

        return new AuthorizeReply
        {
            Allow = allow,
            Reason = reason,
            RecycleDelete = allow &&
                share.IsRecycleEnabled &&
                !ShareEntryPolicy.IsRecycleBinPath(normalized, share.RecycleRootDepth),
            RecycleRootDepth = (uint)share.RecycleRootDepth
        };
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
                share, request.SourcePath, allowRoot: false,
                out string source, out string sourceFull) ||
            !TryResolveClientPath(
                share, request.DestinationPath, allowRoot: false,
                out string destination, out string destinationFull))
            return Deny("invalid rename path");

        if (string.Equals(source, destination, StringComparison.Ordinal))
            return Deny($"invalid rename '{source}' -> '{destination}'");
        if (ShareEntryPolicy.IsReservedForUserWrites(destination, share.RecycleRootDepth))
            return Deny(
                $"rename denied: destination '{destination}' is a reserved Kaimo namespace");

        // Cross-check the native request against the shared storage view. This
        // is not the final TOCTOU guard (the VFS performs that immediately
        // before renameat), but prevents authorization based on stale/type-
        // confused request metadata.
        bool sourceExists = File.Exists(sourceFull) || Directory.Exists(sourceFull);
        bool sourceIsDirectory = sourceExists && Directory.Exists(sourceFull);
        if (!sourceExists || sourceIsDirectory != request.SourceIsDirectory)
            return Deny("rename source no longer matches the native request");

        bool destinationExists = File.Exists(destinationFull) || Directory.Exists(destinationFull);
        bool destinationIsDirectory = destinationExists && Directory.Exists(destinationFull);
        if (destinationExists != request.DestinationExists ||
            (destinationExists &&
             destinationIsDirectory != request.DestinationIsDirectory))
            return Deny("rename destination no longer matches the native request");

        if (!destinationExists && !IsWindowsValidLeaf(destination))
            return Deny($"rename denied: '{destination}' is not valid on Windows");

        if (request.DestinationExists != request.ReplaceIntent)
            return Deny("rename replacement intent does not match destination state");
        if (!request.DestinationExists && request.DestinationIsDirectory)
            return Deny("missing rename destination cannot have an object type");

        string? sourceTraversalDeny = await CheckTraversalAsync(
            user, share.Id, source, cancellationToken);
        if (sourceTraversalDeny is not null)
            return Deny(sourceTraversalDeny);

        string? destinationTraversalDeny = await CheckTraversalAsync(
            user, share.Id, destination, cancellationToken);
        if (destinationTraversalDeny is not null)
            return Deny(destinationTraversalDeny);

        var sourceDelete = await CanDeleteAsync(
            user, share.Id, source, request.SourceIsDirectory,
            cancellationToken);
        if (!sourceDelete.Allowed)
            return Deny(sourceDelete.Reason);

        string destinationParent = ShareRelativePath.GetParent(destination);
        FilePermission createPermission = request.SourceIsDirectory
            ? FilePermission.CreateAppendData
            : FilePermission.CreateWriteData;
        if (!await _acl.HasAccessAsync(
                user, share.Id, destinationParent, true, createPermission)
            .WaitAsync(cancellationToken))
            return Deny(
                $"rename denied: destination parent '{destinationParent}' lacks {createPermission}");

        string replacementSource = "none";
        if (request.DestinationExists)
        {
            var replacementDelete = await CanDeleteAsync(
                user, share.Id, destination,
                request.DestinationIsDirectory, cancellationToken);
            if (!replacementDelete.Allowed)
                return Deny(replacementDelete.Reason);
            replacementSource = replacementDelete.Source;
        }

        if (_cloudSyncOperations is not null &&
            !await _cloudSyncOperations.TryReserveExternalPathMutationAsync(
                share.Id, source, destination, TimeSpan.FromMinutes(2),
                cancellationToken))
            return Deny(
                "rename denied: an overlapping cloud sync is currently running");

        _logger.LogDebug(
            "AuthorizeRename ALLOW: user={User} share={Share} source=[{Source}] destination=[{Destination}] dir={Directory} replace={Replace} sourceDelete={SourceDelete} replacementDelete={ReplacementDelete}",
            request.Username, request.Share, source, destination,
            request.SourceIsDirectory, request.DestinationExists,
            sourceDelete.Source, replacementSource);

        return new AuthorizeReply { Allow = true };
    }

    private async Task<(bool Allowed, string Source, string Reason)> CanDeleteAsync(
        UserContext user, Guid shareId, string normalized, bool isDirectory,
        CancellationToken cancellationToken)
    {
        if (await _acl.HasAccessAsync(
                user, shareId, normalized, isDirectory, FilePermission.Delete)
            .WaitAsync(cancellationToken))
            return (true, "Delete", "");

        string parent = ShareRelativePath.GetParent(normalized);
        if (await _acl.HasAccessAsync(
                user, shareId, parent, true, FilePermission.DeleteSubItems)
            .WaitAsync(cancellationToken))
            return (true, "DeleteSubItems", "");

        return (false, "none",
            $"delete denied: target '{normalized}' lacks Delete and parent '{parent}' lacks DeleteSubItems");
    }

    private static bool IsWindowsValidLeaf(string path) =>
        WindowsFileNameHelper.IsValid(ShareRelativePath.GetFileName(path));
}
