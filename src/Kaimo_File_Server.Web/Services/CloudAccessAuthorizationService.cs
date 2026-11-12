using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;

namespace Kaimo_File_Server.Web.Services;

public sealed class CloudAccessAuthorizationService(
    ICloudAccessRepository repository,
    IManagementAuthService managementAuth,
    IShareRepository? hostShares = null,
    IAclService? hostAcl = null)
{
    public async Task<bool> CanManageAsync(UserContext actor, CloudAccessShare share)
    {
        var scope = await managementAuth.GetAuthorizedDepartmentIdsAsync(
            actor, ManagementPermission.ManageCloudAccess);
        return scope.IsUnrestricted || scope.ScopeIds.Contains(share.DepartmentId);
    }

    public async Task<bool> CanAccessAsync(UserContext actor, CloudAccessShare share)
        => await GetEffectivePermissionAsync(actor, share) is not null;

    /// <summary>
    /// The actor's highest access level on the share, or <c>null</c> when the actor may
    /// not access it. Managers implicitly hold Write; other actors get the highest level
    /// among their matching root-level grants. A share mounted as a folder is additionally
    /// clamped to the host-share ACL at the mount point (managers included), so a mount can
    /// never widen anybody's rights. A read-only provider is clamped at the capability layer
    /// (see the browser view model), not here.
    /// </summary>
    public async Task<CloudAccessPermission?> GetEffectivePermissionAsync(UserContext actor, CloudAccessShare share)
    {
        if (!share.IsEnabled) return null;
        CloudAccessPermission? level;
        if (await CanManageAsync(actor, share))
            level = CloudAccessPermission.Write;
        else
        {
            var principals = actor.Groups.Select(x => x.Id).Append(actor.User.Id);
            level = await repository.GetEffectivePermissionAsync(share.Id, principals);
        }

        if (level is null || !share.IsFolderMode) return level;
        return await ClampToHostAsync(actor, share, level.Value);
    }

    private async Task<CloudAccessPermission?> ClampToHostAsync(
        UserContext actor, CloudAccessShare share, CloudAccessPermission level)
    {
        // Fail closed: orphaned mounts and missing host dependencies grant nothing.
        if (!share.IsMounted || hostShares is null || hostAcl is null) return null;
        var host = await hostShares.GetByIdAsync(share.MountShareId!.Value);
        if (host is null || !host.IsEnabled || host.IsUserHomes) return null;

        var path = share.MountPath!;
        if (!await hostAcl.HasAccessAsync(actor, host.Id, path, true, FilePermission.ListReadData))
            return null;
        if (level == CloudAccessPermission.Read) return level;

        var hostWrite =
            await hostAcl.HasAccessAsync(actor, host.Id, path, true, FilePermission.CreateWriteData) &&
            await hostAcl.HasAccessAsync(actor, host.Id, path, true, FilePermission.Delete);
        return hostWrite ? CloudAccessPermission.Write : CloudAccessPermission.Read;
    }

    /// <summary>
    /// Shares the actor may open. Shares provisioned as a folder are reached only through
    /// their host share, so they are left out unless <paramref name="includeFolderMode"/> is set.
    /// </summary>
    public async Task<List<CloudAccessShare>> GetVisibleSharesAsync(
        UserContext actor, bool includeFolderMode = false)
    {
        var shares = await repository.GetSharesAsync();
        var visible = new List<CloudAccessShare>();
        foreach (var share in shares)
            if ((includeFolderMode || !share.IsFolderMode) && await CanAccessAsync(actor, share))
                visible.Add(share);
        return visible;
    }
}
