using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Web.Services;

public sealed class CloudAccessAuthorizationService(
    ICloudAccessRepository repository,
    IManagementAuthService managementAuth)
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
    /// among their matching root-level grants. A read-only provider is clamped at the
    /// capability layer (see the browser view model), not here.
    /// </summary>
    public async Task<CloudAccessPermission?> GetEffectivePermissionAsync(UserContext actor, CloudAccessShare share)
    {
        if (!share.IsEnabled) return null;
        if (await CanManageAsync(actor, share)) return CloudAccessPermission.Write;
        var principals = actor.Groups.Select(x => x.Id).Append(actor.User.Id);
        return await repository.GetEffectivePermissionAsync(share.Id, principals);
    }

    public async Task<List<CloudAccessShare>> GetVisibleSharesAsync(UserContext actor)
    {
        var shares = await repository.GetSharesAsync();
        var visible = new List<CloudAccessShare>();
        foreach (var share in shares)
            if (await CanAccessAsync(actor, share)) visible.Add(share);
        return visible;
    }
}
