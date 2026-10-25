using Grpc.Core;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for share provisioning (Phase 4). Provides the currently enabled
/// Kaimo shares so the Samba container can mirror them live in its registry
/// (<c>net conf</c>) — the replacement for the FileSystemWatcher/
/// <c>SyncFromDb()</c> mechanism from <c>SmbServer.cs</c>. Thin facade over
/// <see cref="IShareRepository.GetAllEnabledAsync"/>; no share logic is
/// duplicated (the repository already filters disabled shares).
///
/// Visibility (ABE): the hidden flag (<see cref="ShareEntry.IsHidden"/> →
/// <c>browseable = no</c>) for every share, plus a per-user allow list for the
/// home-folder share only (users with an enabled home, plus holders of
/// <see cref="ManagementPermission.AccessHomeContents"/> at any scope), so other users never see
/// it in the share list. Hard share access is unchanged and decided by the VFS
/// connect hook based on real Kaimo ACLs (<see cref="AuthzGrpcService.AuthorizeConnect"/>).
/// </summary>
public sealed class ShareGrpcService : ShareService.ShareServiceBase
{
    private readonly IShareRepository _shares;
    private readonly IUserRepository _users;
    private readonly IScopedRoleAssignmentRepository _assignments;
    private readonly IRoleRepository _roles;
    private readonly IGroupRepository _groups;
    private readonly ILogger<ShareGrpcService> _logger;

    public ShareGrpcService(
        IShareRepository shares, IUserRepository users, IScopedRoleAssignmentRepository assignments,
        IRoleRepository roles, IGroupRepository groups, ILogger<ShareGrpcService> logger)
    {
        _shares = shares;
        _users = users;
        _assignments = assignments;
        _roles = roles;
        _groups = groups;
        _logger = logger;
    }

    public override async Task<ListSharesReply> ListShares(
        ListSharesRequest request, ServerCallContext context)
    {
        CancellationToken cancellationToken =
            context?.CancellationToken ?? CancellationToken.None;
        var defs = await _shares.GetAllEnabledAsync()
            .WaitAsync(cancellationToken);

        var reply = new ListSharesReply();
        foreach (var def in defs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new ShareEntry
            {
                Name = def.Name,
                Path = def.Path,
                IsHidden = def.IsShareHidden,
            };
            if (def.IsUserHomes)
            {
                // Mirrors the per-user root entries HomeDirectoryService grants, plus everyone who may
                // reach other homes; which homes they reach is decided per path by the ACL service.
                var homeAdmins = await ManagementAuthService.GetPermissionHolderUserIdsAsync(
                        _assignments, _roles, _users, _groups,
                        r => (r.ManagementPermissions & ManagementPermission.AccessHomeContents) != 0)
                    .WaitAsync(cancellationToken);
                entry.Restricted = true;
                entry.AllowedUsers.Add((await _users.GetAllAsync().WaitAsync(cancellationToken))
                    .Where(u => u.IsEnabled && (u.HomeDirectoryEnabled || homeAdmins.Contains(u.Id))
                        && SambaName.IsValidUsername(u.Username))
                    .Select(u => u.Username));
            }
            reply.Shares.Add(entry);
        }

        _logger.LogInformation("ListShares -> {Count} enabled shares.", reply.Shares.Count);
        return reply;
    }
}
