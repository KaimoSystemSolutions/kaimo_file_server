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

        var share = await _shares.GetByNameAsync(request.Share);
        if (share is null)
            return Deny($"unknown share '{request.Share}'");

        bool allow = await _auth.CanAccessShareAsync(share.Id, user.Id);

        _logger.LogInformation(
            "AuthorizeConnect: user={User} share={Share} -> {Decision}",
            request.Username, request.Share, allow ? "ALLOW" : "DENY");

        return new AuthorizeReply { Allow = allow, Reason = allow ? "" : "acl denied" };
    }

    /// <summary>
    /// File/path authorization — mirrors <c>FileService.OpenAsync</c> exactly:
    /// non-existent + create → parent needs <c>CreateWriteData</c>; existent →
    /// write needs <c>CreateWriteData</c>, every non-pure-write open additionally
    /// needs <c>ListReadData</c> (read is checked independently). Non-existent
    /// files without create intent are not an ACL deny (Samba treats that as
    /// "not found").
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeOpen(
        AuthorizeOpenRequest request, ServerCallContext context)
    {
        var user = await _auth.ResolveUserContextAsync(request.Username);
        if (user is null)
            return Deny($"unknown user '{request.Username}'");

        var share = await _shares.GetByNameAsync(request.Share);
        if (share is null)
            return Deny($"unknown share '{request.Share}'");

        string normalized = ShareRelativePath.Normalize(request.Path);
        string full = Path.Combine(share.Path, normalized);
        bool exists = File.Exists(full) || Directory.Exists(full);
        bool isDir = Directory.Exists(full);

        bool allow;
        string reason = "";
        if (!exists)
        {
            // No target + no create intent → "not found", no ACL deny.
            if (!request.WantsCreate)
            {
                allow = true;
            }
            else
            {
                string parent = ShareRelativePath.GetParent(normalized);
                allow = await _acl.HasAccessAsync(
                    user, share.Id, parent, true, FilePermission.CreateWriteData);
                if (!allow) reason = $"create denied: parent '{parent}' lacks CreateWriteData";
            }
        }
        else
        {
            allow = true;
            if (request.WantWrite &&
                !await _acl.HasAccessAsync(user, share.Id, normalized, isDir, FilePermission.CreateWriteData))
            {
                allow = false;
                reason = "write denied: CreateWriteData";
            }

            // Every non-pure-write open reads the entry (also delete-only).
            bool wantsRead = request.WantRead || !request.WantWrite;
            if (allow && wantsRead &&
                !await _acl.HasAccessAsync(user, share.Id, normalized, isDir, FilePermission.ListReadData))
            {
                allow = false;
                reason = "read denied: ListReadData";
            }

            if (allow && request.WantDelete)
            {
                var deleteDecision = await CanDeleteAsync(user, share.Id, normalized, isDir);
                if (!deleteDecision.Allowed)
                {
                    allow = false;
                    reason = deleteDecision.Reason;
                }
            }
        }

        // ALLOW stays at debug (otherwise the readdir filter floods the log); every
        // DENY comes at info WITH reason → so it's immediately visible whether (and
        // why) the ACL blocks a write. If there's NO DENY line here for a failed write,
        // the ACL allowed it → the rejection is filesystem-side.
        if (allow)
            _logger.LogDebug(
                "AuthorizeOpen ALLOW: user={User} share={Share} path=[{Path}] r={R} w={W} c={C} d={D}",
                request.Username, request.Share, request.Path,
                request.WantRead, request.WantWrite, request.WantsCreate, request.WantDelete);
        else
            _logger.LogInformation(
                "AuthorizeOpen DENY: user={User} share={Share} path=[{Path}] r={R} w={W} c={C} d={D} — {Reason}",
                request.Username, request.Share, request.Path,
                request.WantRead, request.WantWrite, request.WantsCreate, request.WantDelete, reason);

        return new AuthorizeReply { Allow = allow, Reason = allow ? "" : reason };
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

        var share = await _shares.GetByNameAsync(request.Share);
        if (share is null)
            return Deny($"unknown share '{request.Share}'");

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
